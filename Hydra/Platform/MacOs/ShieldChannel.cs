using System.Buffers;
using System.Buffers.Binary;
using System.Collections;
using System.Collections.Concurrent;
using System.Runtime.CompilerServices;
using MessagePack;
using Microsoft.Extensions.Logging;

namespace Hydra.Platform.MacOs;

// one message on the shield ipc: its kind and its fields keyed by name.
// field values are null, bool, long (int or ulong on send), double, string, byte[], arrays (object?[]; any IList on send)
// and nested maps (IReadOnlyDictionary<string, object?>). a uint64 above long.MaxValue decodes as ulong.
internal sealed record ShieldMessage(string Kind, IReadOnlyDictionary<string, object?> Fields)
{
    private static readonly IReadOnlyDictionary<string, object?> NoFields = new Dictionary<string, object?>();

    public ShieldMessage(string kind) : this(kind, NoFields) { }

    public object? this[string name] => Fields.GetValueOrDefault(name);

    public string? GetString(string name) => this[name] as string;

    public long? GetLong(string name) => this[name] is long value ? value : null;
}

// framing, codec and ack matching for the hydra-shield helper, over plain streams.
// every message, both directions, is a 4-byte little-endian length then a messagepack map: "kind" plus the fields.
//
//   c# → shield                                          shield → c#
//   shield {seq:int, state:"hide"|"show"|"debug"}        ack {seq}         matched by seq, see SendAndAwaitAck
//   wifi                                                 wifiAuth {status:int}, ssid {name:string|nil}
//   osd {text}
//   transferBegin {totalBytes:long, fileCount:int, isSender:bool, names:[string]}
//   transferFile {name}, transferProgress {bytes:long, speed:double}
//   transferDone, transferError {message}, transferClose  transferCancel
//   echo {payload:any}   tests only: decoded, re-encoded  echo {payload}
//
// unknown kinds are passed to the caller, which ignores what it doesn't know. frames over 16 MiB end the channel.
internal sealed class ShieldChannel(Stream input, Stream output, ILogger log)
{
    internal const int MaxFrame = 16 * 1024 * 1024;
    private const int MaxDepth = 64;

    private readonly SemaphoreSlim _writeLock = new(1, 1);
    private readonly ConcurrentDictionary<long, TaskCompletionSource<bool>> _pendingAcks = new();
    private int _nextSeq;
    private volatile bool _ended;
    // a write given up mid-frame leaves the far end unable to find the next one, so it ends the channel
    private volatile bool _broken;
    private readonly CancellationTokenSource _brokenCts = new();

    // whether a send gave up mid-frame, which ended the channel, for tests
    public bool IsBroken => _broken;

    // runs once when a send gives up mid-frame, so the owner can end the far end too
    public Action? OnBroken { get; init; }

    public async ValueTask Send(ShieldMessage message, CancellationToken ct)
    {
        var body = Encode(message);
        var frame = new byte[4 + body.Length];
        BinaryPrimitives.WriteInt32LittleEndian(frame, body.Length);
        body.CopyTo(frame, 4);
        await _writeLock.WaitAsync(ct);
        var broke = false;
        try
        {
            if (_broken) throw new IOException("an earlier send gave up mid-frame");
            var write = WriteFrame(frame, ct);
            try
            {
                // a pipe write need not honour the token, so the wait gives up on its own
                await write.WaitAsync(ct);
            }
            catch (OperationCanceledException)
            {
                Observe(write);
                Break();
                broke = true;
                throw;
            }
        }
        finally
        {
            _writeLock.Release();
            // after the lock, as ending the far end may block, and every other send fails fast on _broken meanwhile
            if (broke) EndFarEnd();
        }
    }

    // an abandoned task's fault, observed so it never surfaces as unobserved
    private static void Observe(Task task) => _ = task.ContinueWith(static t => t.Exception, TaskContinuationOptions.OnlyOnFaulted);

    // ends the channel: its read ends, and every pending ack fails
    private void Break()
    {
        _broken = true;
        log.LogWarning("Shield channel write gave up mid-frame; ending the channel");
        _brokenCts.Cancel();
        FailPendingAcks();
    }

    private void EndFarEnd()
    {
        try { OnBroken?.Invoke(); }
        catch (Exception e) { log.LogWarning(e, "Ending the broken shield channel's far end failed"); }
    }

    private void FailPendingAcks()
    {
        _ended = true;
        foreach (var waiter in _pendingAcks.Values)
            waiter.TrySetResult(false);
    }

    private async Task WriteFrame(byte[] frame, CancellationToken ct)
    {
        await output.WriteAsync(frame, ct);
        await output.FlushAsync(ct);
    }

    // sends kind with the next seq added to fields; true once the matching ack arrives, false when the send and ack together outlast
    // timeout, on a failed write or channel end
    public async ValueTask<bool> SendAndAwaitAck(string kind, IReadOnlyDictionary<string, object?>? fields, TimeSpan timeout, CancellationToken ct)
    {
        long seq = Interlocked.Increment(ref _nextSeq);
        var waiter = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        _pendingAcks[seq] = waiter;
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(ct);
        deadline.CancelAfter(timeout);
        try
        {
            // checked after registering, so an end racing us either sees the waiter or is seen here
            if (_ended) return false;
            var withSeq = new Dictionary<string, object?>();
            if (fields != null)
                foreach (var pair in fields)
                    withSeq[pair.Key] = pair.Value;
            withSeq["seq"] = seq;
            await Send(new ShieldMessage(kind, withSeq), deadline.Token);
            return await waiter.Task.WaitAsync(deadline.Token);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            return false;
        }
        catch (Exception e) when (e is IOException or ObjectDisposedException)
        {
            return false;
        }
        finally
        {
            _pendingAcks.TryRemove(seq, out _);
        }
    }

    // the read loop; run it once. acks complete their waiters, everything else is yielded.
    // completes at end of stream (mid-frame included) or on a bad length, failing every pending ack.
    public async IAsyncEnumerable<ShieldMessage> ReadAll([EnumeratorCancellation] CancellationToken ct)
    {
        using var read = CancellationTokenSource.CreateLinkedTokenSource(ct, _brokenCts.Token);
        try
        {
            while (await ReadFrame(read.Token) is { } frame)
            {
                var message = Decode(frame);
                if (message == null) continue;
                if (message.Kind == "ack")
                {
                    if (message.GetLong("seq") is { } seq && _pendingAcks.TryGetValue(seq, out var waiter)) waiter.TrySetResult(true);
                    continue;
                }
                yield return message;
            }
        }
        finally
        {
            FailPendingAcks();
        }
    }

    // one frame's body, or null when the channel is over
    private async ValueTask<byte[]?> ReadFrame(CancellationToken ct)
    {
        try
        {
            var header = new byte[4];
            if (!await Fill(header, ct)) return null;
            var length = BinaryPrimitives.ReadUInt32LittleEndian(header);
            if (length > MaxFrame)
            {
                log.LogWarning("Shield frame of {Length} bytes exceeds {Max}; closing the channel", length, MaxFrame);
                return null;
            }
            var body = new byte[length];
            return await Fill(body, ct) ? body : null;
        }
        catch (OperationCanceledException) when (_broken)
        {
            return null;
        }
        catch (IOException e)
        {
            if (log.IsEnabled(LogLevel.Debug)) log.LogDebug("Shield channel read failed: {Error}", e.Message);
            return null;
        }
        catch (ObjectDisposedException)
        {
            return null;
        }
    }

    // false if the stream ends before buffer is full
    private async ValueTask<bool> Fill(byte[] buffer, CancellationToken ct)
    {
        var filled = 0;
        while (filled < buffer.Length)
        {
            // a pipe read need not honour the token either, so the wait gives up on its own; the channel is over then,
            // and the read left behind may still fault
            var pending = input.ReadAsync(buffer.AsMemory(filled), ct).AsTask();
            int read;
            try
            {
                read = await pending.WaitAsync(ct);
            }
            catch (OperationCanceledException)
            {
                Observe(pending);
                throw;
            }
            if (read == 0) return false;
            filled += read;
        }
        return true;
    }

    internal static byte[] Encode(ShieldMessage message)
    {
        var buffer = new ArrayBufferWriter<byte>();
        var writer = new MessagePackWriter(buffer);
        var count = message.Fields.Count + (message.Fields.ContainsKey("kind") ? 0 : 1);
        writer.WriteMapHeader(count);
        writer.Write("kind");
        writer.Write(message.Kind);
        foreach (var pair in message.Fields)
        {
            if (pair.Key == "kind") continue;
            writer.Write(pair.Key);
            WriteValue(ref writer, pair.Value);
        }
        writer.Flush();
        return buffer.WrittenSpan.ToArray();
    }

    // Hydra's own messages are never hostile, so only a cycle needs stopping, before it overflows the stack
    private static void WriteValue(ref MessagePackWriter writer, object? value)
    {
        RuntimeHelpers.EnsureSufficientExecutionStack();
        switch (value)
        {
            case null: writer.WriteNil(); break;
            case bool b: writer.Write(b); break;
            case int i: writer.Write(i); break;
            case long l: writer.Write(l); break;
            case ulong u: writer.Write(u); break;
            case double d: writer.Write(d); break;
            case string s: writer.Write(s); break;
            case byte[] bytes: writer.Write(bytes); break;
            case IReadOnlyDictionary<string, object?> map:
                writer.WriteMapHeader(map.Count);
                foreach (var pair in map)
                {
                    writer.Write(pair.Key);
                    WriteValue(ref writer, pair.Value);
                }
                break;
            case IList list:
                writer.WriteArrayHeader(list.Count);
                foreach (var item in list)
                    WriteValue(ref writer, item);
                break;
            default: throw new ArgumentException($"Shield messages cannot carry a {value.GetType().Name}");
        }
    }

    // null, logged, for a frame that is not a map with a string kind; the framing around it is still sound
    private ShieldMessage? Decode(byte[] frame)
    {
        try
        {
            var reader = new MessagePackReader(frame);
            if (ReadValue(ref reader) is not IReadOnlyDictionary<string, object?> map || map.GetValueOrDefault("kind") is not string kind)
            {
                log.LogWarning("Shield frame is not a map with a kind; ignored");
                return null;
            }
            var fields = new Dictionary<string, object?>(map);
            fields.Remove("kind");
            return new ShieldMessage(kind, fields);
        }
        catch (Exception e) when (e is MessagePackSerializationException or EndOfStreamException or InvalidDataException)
        {
            log.LogWarning("Shield frame could not be decoded; ignored: {Error}", e.Message);
            return null;
        }
    }

    // reader.Depth counts the arrays and maps around the value, which a hostile frame could nest without end
    private static object? ReadValue(ref MessagePackReader reader)
    {
        if (reader.Depth > MaxDepth) throw new InvalidDataException("Shield frame nested too deeply");
        switch (reader.NextMessagePackType)
        {
            case MessagePackType.Nil:
                reader.ReadNil();
                return null;
            case MessagePackType.Boolean:
                return reader.ReadBoolean();
            case MessagePackType.Integer:
                if (reader.NextCode != MessagePackCode.UInt64) return reader.ReadInt64();
                var unsigned = reader.ReadUInt64();
                return unsigned <= long.MaxValue ? (long)unsigned : unsigned;
            case MessagePackType.Float:
                return reader.ReadDouble();
            case MessagePackType.String:
                return reader.ReadString();
            case MessagePackType.Binary:
                return reader.ReadBytes()?.ToArray();
            case MessagePackType.Array:
                var items = new object?[reader.ReadArrayHeader()];
                reader.Depth++;
                for (var i = 0; i < items.Length; i++)
                    items[i] = ReadValue(ref reader);
                reader.Depth--;
                return items;
            case MessagePackType.Map:
                var count = reader.ReadMapHeader();
                var map = new Dictionary<string, object?>(count);
                reader.Depth++;
                for (var i = 0; i < count; i++)
                {
                    if (reader.NextMessagePackType != MessagePackType.String) throw new InvalidDataException("Shield map key is not a string");
                    var key = reader.ReadString()!;
                    map[key] = ReadValue(ref reader);
                }
                reader.Depth--;
                return map;
            default:
                throw new InvalidDataException($"Shield frame carries an unsupported {reader.NextMessagePackType}");
        }
    }
}

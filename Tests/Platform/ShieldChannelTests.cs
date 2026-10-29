using System.IO.Pipelines;
using Hydra.Platform.MacOs;
using Microsoft.Extensions.Logging.Abstractions;

namespace Tests.Platform;

[TestFixture]
public class ShieldChannelTests
{
    // a channel, its two pipes, and a peer channel standing in for the shield on the far end
    private sealed class Link
    {
        public readonly Pipe ToHydra = new();
        public readonly Pipe ToShield = new();
        public readonly ShieldChannel Hydra;
        public readonly ShieldChannel Shield;

        public Link()
        {
            Hydra = new ShieldChannel(ToHydra.Reader.AsStream(), ToShield.Writer.AsStream(), NullLogger.Instance);
            Shield = new ShieldChannel(ToShield.Reader.AsStream(), ToHydra.Writer.AsStream(), NullLogger.Instance);
        }
    }

    // a stream that hands out one byte per read
    private sealed class OneByteStream(byte[] data) : Stream
    {
        private readonly MemoryStream _inner = new(data);
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override int Read(byte[] buffer, int offset, int count) => _inner.Read(buffer, offset, Math.Min(count, 1));
        public override void Flush() { }
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }

    private static Task<List<ShieldMessage>> Drain(ShieldChannel channel) => Task.Run(async () =>
    {
        var messages = new List<ShieldMessage>();
        await foreach (var message in channel.ReadAll(CancellationToken.None))
            messages.Add(message);
        return messages;
    });

    private static async Task<ShieldMessage> Next(IAsyncEnumerator<ShieldMessage> reader)
    {
        Assert.That(await Settled(reader.MoveNextAsync().AsTask()), Is.True, "the channel ended");
        return reader.Current;
    }

    // the task's result; throws, rather than failing an assert that a multiple scope would defer, if it never finishes
    private static async Task<T> Settled<T>(Task<T> task)
    {
        if (!await Settles(task)) throw new TimeoutException($"nothing settled within {Bound}");
        return await task;
    }

    private static byte[] Frame(byte[] body)
    {
        var frame = new byte[4 + body.Length];
        BitConverter.TryWriteBytes(frame.AsSpan(0, 4), body.Length);
        if (!BitConverter.IsLittleEndian) Array.Reverse(frame, 0, 4);
        body.CopyTo(frame, 4);
        return frame;
    }

    [Test]
    public async Task RoundTrip_EveryValueType()
    {
        var link = new Link();
        var nested = new Dictionary<string, object?>
        {
            ["inner"] = new Dictionary<string, object?> { ["deep"] = new object?[] { true, null, "z" } },
            ["n"] = 5L
        };
        var sent = new Dictionary<string, object?>
        {
            ["text"] = "æøå 🐍\nsecond line",
            ["empty"] = "",
            ["min"] = long.MinValue,
            ["max"] = long.MaxValue,
            ["zero"] = 0L,
            ["minusOne"] = -1L,
            ["int"] = 7,
            ["intMin"] = int.MinValue,
            ["huge"] = ulong.MaxValue,
            ["yes"] = true,
            ["no"] = false,
            ["real"] = 3.25,
            ["tiny"] = double.Epsilon,
            ["nothing"] = null,
            ["bytes"] = new byte[] { 0, 1, 127, 128, 255 },
            ["noBytes"] = Array.Empty<byte>(),
            ["names"] = new[] { "a.txt", "b.txt" },
            ["mixed"] = new object?[] { 1L, "x", null, "\t"u8.ToArray() },
            ["payload"] = nested
        };
        await using var reader = link.Shield.ReadAll(CancellationToken.None).GetAsyncEnumerator();

        await link.Hydra.Send(new ShieldMessage("echo", sent), CancellationToken.None);
        var got = await Next(reader);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(got.Kind, Is.EqualTo("echo"));
            Assert.That(got.Fields.Keys, Is.EquivalentTo(sent.Keys));
            Assert.That(got["text"], Is.EqualTo("æøå 🐍\nsecond line"));
            Assert.That(got["empty"], Is.EqualTo(""));
            Assert.That(got["min"], Is.EqualTo(long.MinValue));
            Assert.That(got["max"], Is.EqualTo(long.MaxValue));
            Assert.That(got["zero"], Is.Zero);
            Assert.That(got["minusOne"], Is.EqualTo(-1L));
            Assert.That(got.GetLong("int"), Is.EqualTo(7L));
            Assert.That(got.GetLong("intMin"), Is.EqualTo((long)int.MinValue));
            Assert.That(got["huge"], Is.EqualTo(ulong.MaxValue));
            Assert.That(got["yes"], Is.True);
            Assert.That(got["no"], Is.False);
            Assert.That(got["real"], Is.EqualTo(3.25));
            Assert.That(got["tiny"], Is.EqualTo(double.Epsilon));
            Assert.That(got.Fields.ContainsKey("nothing"), Is.True);
            Assert.That(got["nothing"], Is.Null);
            Assert.That(got["bytes"], Is.EqualTo(new byte[] { 0, 1, 127, 128, 255 }));
            Assert.That(got["noBytes"], Is.EqualTo(Array.Empty<byte>()));
            Assert.That(got["names"], Is.EqualTo(new object?[] { "a.txt", "b.txt" }));
            Assert.That(got["mixed"], Is.EqualTo(new object?[] { 1L, "x", null, "\t"u8.ToArray() }));
            Assert.That(got["payload"], Is.EqualTo(nested));
        }
    }

    [Test]
    public async Task Send_WritesLittleEndianLengthThenMap()
    {
        var output = new MemoryStream();
        var channel = new ShieldChannel(new MemoryStream(), output, NullLogger.Instance);

        await channel.Send(new ShieldMessage("wifi"), CancellationToken.None);

        // 11 bytes: fixmap(1), fixstr "kind", fixstr "wifi"
        byte[] expected = [0x0b, 0x00, 0x00, 0x00, 0x81, 0xa4, (byte)'k', (byte)'i', (byte)'n', (byte)'d', 0xa4, (byte)'w', (byte)'i', (byte)'f', (byte)'i'];
        Assert.That(output.ToArray(), Is.EqualTo(expected));
    }

    [Test]
    public async Task FrameDeliveredOneByteAtATime_StillDecodes()
    {
        var body = ShieldChannel.Encode(new ShieldMessage("ssid", new Dictionary<string, object?> { ["name"] = "home\nnet" }));
        var channel = new ShieldChannel(new OneByteStream(Frame(body)), Stream.Null, NullLogger.Instance);

        var messages = await Settled(Drain(channel));

        Assert.That(messages, Has.Count.EqualTo(1));
        using (Assert.EnterMultipleScope())
        {
            Assert.That(messages[0].Kind, Is.EqualTo("ssid"));
            Assert.That(messages[0].GetString("name"), Is.EqualTo("home\nnet"));
        }
    }

    [Test]
    public async Task TwoFramesInOneWrite_BothDecode()
    {
        var link = new Link();
        var first = Frame(ShieldChannel.Encode(new ShieldMessage("wifiAuth", new Dictionary<string, object?> { ["status"] = 3 })));
        var second = Frame(ShieldChannel.Encode(new ShieldMessage("ssid", new Dictionary<string, object?> { ["name"] = null })));
        await using var reader = link.Hydra.ReadAll(CancellationToken.None).GetAsyncEnumerator();

        await link.ToHydra.Writer.WriteAsync(first.Concat(second).ToArray());
        var a = await Next(reader);
        var b = await Next(reader);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(a.Kind, Is.EqualTo("wifiAuth"));
            Assert.That(a.GetLong("status"), Is.EqualTo(3L));
            Assert.That(b.Kind, Is.EqualTo("ssid"));
            Assert.That(b.Fields.ContainsKey("name"), Is.True);
            Assert.That(b["name"], Is.Null);
        }
    }

    [Test]
    public async Task Acks_OutOfOrder_CompleteTheirOwnWaiters_AndUnknownSeqIsIgnored()
    {
        var link = new Link();
        var hydraReading = Drain(link.Hydra);
        await using var shield = link.Shield.ReadAll(CancellationToken.None).GetAsyncEnumerator();

        var hide = link.Hydra.SendAndAwaitAck("shield", new Dictionary<string, object?> { ["state"] = "hide" }, Bound, CancellationToken.None).AsTask();
        var hideSeq = (await Next(shield)).GetLong("seq");
        var show = link.Hydra.SendAndAwaitAck("shield", new Dictionary<string, object?> { ["state"] = "show" }, Bound, CancellationToken.None).AsTask();
        var showMessage = await Next(shield);
        var showSeq = showMessage.GetLong("seq");
        using (Assert.EnterMultipleScope())
        {
            Assert.That(showMessage.GetString("state"), Is.EqualTo("show"));
            Assert.That(hideSeq, Is.Not.EqualTo(showSeq));
        }

        await link.Shield.Send(new ShieldMessage("ack", new Dictionary<string, object?> { ["seq"] = 999_999L }), CancellationToken.None);
        await link.Shield.Send(new ShieldMessage("ack", new Dictionary<string, object?> { ["seq"] = showSeq }), CancellationToken.None);
        using (Assert.EnterMultipleScope())
        {
            Assert.That(await Settled(show), Is.True);
            // the read loop handled the unknown ack before the one that completed show
            Assert.That(hide.IsCompleted, Is.False, "an ack for another seq completed hide");
        }

        await link.Shield.Send(new ShieldMessage("ack", new Dictionary<string, object?> { ["seq"] = hideSeq }), CancellationToken.None);
        Assert.That(await Settled(hide), Is.True);

        await link.ToHydra.Writer.CompleteAsync();
        Assert.That(await Settled(hydraReading), Is.Empty, "acks are routed, not yielded");
    }

    [Test]
    public async Task SendAndAwaitAck_TimesOut_ReturnsFalse()
    {
        var link = new Link();
        _ = Drain(link.Hydra);

        var acked = await link.Hydra.SendAndAwaitAck("shield", new Dictionary<string, object?> { ["state"] = "debug" }, TimeSpan.FromMilliseconds(20),
            CancellationToken.None);

        Assert.That(acked, Is.False);
    }

    // a shield that stopped reading fills the pipe, and a write to it never returns, whatever the token says
    [Test]
    public async Task SendAndAwaitAck_WriteThatNeverReturns_GivesUpWithinTheTimeout()
    {
        var channel = new ShieldChannel(new MemoryStream(), new NeverWrittenStream(), NullLogger.Instance);

        var acked = channel.SendAndAwaitAck("shield", new Dictionary<string, object?> { ["state"] = "show" }, TimeSpan.FromMilliseconds(50),
            CancellationToken.None).AsTask();

        using (Assert.EnterMultipleScope())
        {
            Assert.That(await Settled(acked), Is.False);
            Assert.That(async () => await channel.Send(new ShieldMessage("osd"), CancellationToken.None).AsTask().WaitAsync(Bound),
                Throws.InstanceOf<IOException>(), "a frame given up mid-way leaves the channel broken");
        }
    }

    // a stalled write ends the channel: its read ends though the stream never does, every pending ack fails, and the owner is told
    [Test]
    public async Task StalledWrite_EndsTheChannel()
    {
        var toHydra = new Pipe();
        var broken = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var channel = new ShieldChannel(toHydra.Reader.AsStream(), new NeverWrittenStream { WritesBeforeStalling = 1 }, NullLogger.Instance)
        {
            OnBroken = broken.SetResult,
        };
        var reading = Drain(channel);
        var pending = channel.SendAndAwaitAck("shield", new Dictionary<string, object?> { ["state"] = "hide" }, Bound, CancellationToken.None).AsTask();

        using var timeout = new CancellationTokenSource(TimeSpan.FromMilliseconds(50));
        var stalled = channel.Send(new ShieldMessage("osd"), timeout.Token).AsTask();
        Assert.That(async () => await stalled, Throws.InstanceOf<OperationCanceledException>());

        using (Assert.EnterMultipleScope())
        {
            Assert.That(await Settled(reading), Is.Empty);
            Assert.That(await Settled(pending), Is.False);
            Assert.That(await Settles(broken.Task), Is.True);
            Assert.That(channel.IsBroken, Is.True);
        }
    }

    // ending the far end may block, so it runs once the write lock is let go: a send made from it fails at once
    [Test]
    public async Task StalledWrite_EndsTheFarEnd_AfterLettingGoOfTheWriteLock()
    {
        var toHydra = new Pipe();
        var sendFromBroken = new TaskCompletionSource<Task>(TaskCreationOptions.RunContinuationsAsynchronously);
        var self = new TaskCompletionSource<ShieldChannel>();
        var channel = new ShieldChannel(toHydra.Reader.AsStream(), new NeverWrittenStream(), NullLogger.Instance)
        {
            OnBroken = () => sendFromBroken.SetResult(self.Task.Result.Send(new ShieldMessage("osd"), CancellationToken.None).AsTask()),
        };
        self.SetResult(channel);

        using var timeout = new CancellationTokenSource(TimeSpan.FromMilliseconds(50));
        var stalled = channel.Send(new ShieldMessage("osd"), timeout.Token).AsTask();
        Assert.That(async () => await stalled, Throws.InstanceOf<OperationCanceledException>());

        var send = await sendFromBroken.Task.WaitAsync(Bound);
        Assert.That(send.IsCompleted, Is.True, "the write lock was still held");
        Assert.That(async () => await send, Throws.InstanceOf<IOException>());
    }

    // a stream whose writes park forever, ignoring cancellation, after the first few
    private sealed class NeverWrittenStream : Stream
    {
        private int _written;
        public int WritesBeforeStalling { get; init; }
        public override bool CanRead => false;
        public override bool CanSeek => false;
        public override bool CanWrite => true;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override void Flush() { }
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default) =>
            Interlocked.Increment(ref _written) <= WritesBeforeStalling ? ValueTask.CompletedTask : new(new TaskCompletionSource().Task);
    }

    [Test]
    public async Task EndOfStreamMidFrame_CompletesReading_AndFailsPendingAcks()
    {
        var link = new Link();
        var hydraReading = Drain(link.Hydra);
        await using var shield = link.Shield.ReadAll(CancellationToken.None).GetAsyncEnumerator();
        var pending = link.Hydra.SendAndAwaitAck("shield", new Dictionary<string, object?> { ["state"] = "hide" }, Bound, CancellationToken.None).AsTask();
        await Next(shield);

        // a header promising 10 bytes, then 3 of them, then the end
        await link.ToHydra.Writer.WriteAsync(new byte[] { 10, 0, 0, 0, 0x81, 0xa4, (byte)'k' });
        await link.ToHydra.Writer.CompleteAsync();

        using (Assert.EnterMultipleScope())
        {
            Assert.That(await Settled(hydraReading), Is.Empty);
            Assert.That(await Settled(pending), Is.False);
        }
    }

    [Test]
    public async Task SendAndAwaitAck_AfterChannelEnded_ReturnsFalse()
    {
        var link = new Link();
        var hydraReading = Drain(link.Hydra);
        await link.ToHydra.Writer.CompleteAsync();
        await Settled(hydraReading);

        var acked = link.Hydra.SendAndAwaitAck("shield", new Dictionary<string, object?> { ["state"] = "show" }, Bound, CancellationToken.None).AsTask();

        Assert.That(await Settled(acked), Is.False);
    }

    [TestCase((uint)ShieldChannel.MaxFrame + 1)]
    [TestCase(uint.MaxValue)]
    public async Task OversizedLength_EndsTheChannel_WithoutReadingTheBody(uint length)
    {
        var link = new Link();
        var hydraReading = Drain(link.Hydra);
        var header = BitConverter.GetBytes(length);
        if (!BitConverter.IsLittleEndian) Array.Reverse(header);

        // the writer stays open: a reader waiting for the body would never finish
        await link.ToHydra.Writer.WriteAsync(header);

        Assert.That(await Settled(hydraReading), Is.Empty);
    }

    [Test]
    public async Task UnknownKind_IsYieldedUnchanged()
    {
        var link = new Link();
        await using var reader = link.Hydra.ReadAll(CancellationToken.None).GetAsyncEnumerator();

        await link.Shield.Send(new ShieldMessage("somethingNew", new Dictionary<string, object?> { ["x"] = 1L, ["y"] = "two" }), CancellationToken.None);
        var got = await Next(reader);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(got.Kind, Is.EqualTo("somethingNew"));
            Assert.That(got.Fields, Is.EqualTo(new Dictionary<string, object?> { ["x"] = 1L, ["y"] = "two" }));
        }
    }
}

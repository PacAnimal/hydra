using ByteSizeLib;
using Cathedral.Extensions;
using Hydra.Relay;
using Microsoft.Extensions.Logging;

namespace Hydra.FileTransfer;

public sealed class FileTransferService : IDisposable
{
    private readonly IFileTransferDialog _dialog;
    private readonly IDropTargetResolver _dropTargetResolver;
    private readonly ILogger<FileTransferService> _log;
    private readonly Lock _lock = new();

    private const double SpeedMinElapsedSec = 0.5;  // avoid nonsense speed values in the first half-second
    private const int WatchdogDisposalWaitMs = 1000;  // how long to wait for in-flight watchdog callback on cleanup

    private readonly int _watchdogTimeoutMs;

    // copy buffer (set on copy hotkey; consumed on paste hotkey)
    private FileCopyState? _copyBuffer;

    // sender side
    private CancellationTokenSource? _sendCts;
    private string? _sendTargetHost;
    private IRelaySender? _sendRelay;
    private Action<string>? _pasteOsd;       // one-shot OSD callback fired when paste outcome is known
    private TaskCompletionSource<bool>? _sendAcceptTcs;  // completed when receiver sends FileTransferAccepted

    // receiver side
    private ReceiverTransfer? _receiver;
    private IRelaySender? _recvRelay;

    // true while any transfer is in flight (sending or receiving) — used to block new transfers
    public bool FileTransferOngoing { get { lock (_lock) return _sendCts != null || _receiver != null; } }

    // stores source host + paths from the last copy hotkey press
    public void SetCopyBuffer(string sourceHost, List<string> paths)
    {
        lock (_lock) _copyBuffer = new FileCopyState(sourceHost, [.. paths]);
        _log.LogInformation("Copy buffer set: {Count} item(s) from {Host}", paths.Count, sourceHost);
    }

    // called when a FileSelectionResponse arrives for a remote copy; returns the OSD text to display
    public string HandleSelectionResponse(string sourceHost, string json)
    {
        var msg = json.FromSaneJson<FileSelectionResponseMessage>();
        if (msg?.NotFocusedMessage != null)
            return msg.NotFocusedMessage;
        if (msg?.Paths is { Length: > 0 })
        {
            lock (_lock) _copyBuffer = new FileCopyState(sourceHost, msg.Paths);
            _log.LogInformation("Copy buffer set from {Host}: {Count} item(s)", sourceHost, msg.Paths.Length);
            var n = msg.Paths.Length;
            return $"{n} {(n == 1 ? "item" : "items")} copied";
        }
        return "Nothing to copy...";
    }

    public FileCopyState? GetCopyBuffer() { lock (_lock) return _copyBuffer; }
    public void ClearCopyBuffer() { lock (_lock) _copyBuffer = null; }

    private static double CalcSpeed(long startTick, long bytes)
    {
        var elapsed = (Environment.TickCount64 - startTick) / 1000.0;
        return elapsed > SpeedMinElapsedSec ? bytes / elapsed : 0;
    }

    // swaps out _receiver and _recvRelay under lock into the out params.
    private void TryClearReceiver(out ReceiverTransfer? receiver, out IRelaySender? recvRelay)
    {
        lock (_lock)
        {
            receiver = _receiver; _receiver = null;
            recvRelay = _recvRelay; _recvRelay = null;
        }
    }

    // swaps out _sendCts under lock, cancels and disposes it; also clears host/relay fields.
    // returns true if there was an active send to cancel.
    private bool TryCancelSend(out string? targetHost, out IRelaySender? sendRelay)
    {
        CancellationTokenSource? cts;
        lock (_lock)
        {
            cts = _sendCts; _sendCts = null;
            targetHost = _sendTargetHost; _sendTargetHost = null;
            sendRelay = _sendRelay; _sendRelay = null;
            _sendAcceptTcs = null;  // cancel token propagates to WaitAsync; clear for cleanup
        }
        if (cts == null) return false;
        cts.Cancel();
        cts.Dispose();
        return true;
    }

    // fires _pasteOsd with message (if set) then clears it — safe to call from any thread
    private void FirePasteOsd(string message)
    {
        Action<string>? osd;
        lock (_lock) { osd = _pasteOsd; _pasteOsd = null; }
        osd?.Invoke(message);
    }

    private void ClearPasteOsd() { lock (_lock) _pasteOsd = null; }

    public static bool IsFileTransferMessage(MessageKind kind) => kind is
        MessageKind.FileTransferStart or MessageKind.FileTransferChunk or
        MessageKind.FileTransferDone or MessageKind.FileTransferAbort or
        MessageKind.FileTransferAccepted;

    internal static FileTransferService Null() =>
        new(new NullFileTransferDialog(), new NullDropTargetResolver(), Microsoft.Extensions.Logging.Abstractions.NullLogger<FileTransferService>.Instance);

    public FileTransferService(IFileTransferDialog dialog, IDropTargetResolver dropTargetResolver, ILogger<FileTransferService> log, int watchdogTimeoutMs = 30_000)
    {
        _dialog = dialog;
        _dropTargetResolver = dropTargetResolver;
        _log = log;
        _watchdogTimeoutMs = watchdogTimeoutMs;
        // subscribe once — reads current state on each cancel click
        dialog.CancelRequested += HandleCancelRequested;
    }

    // called when relay disconnects or peer departs during an active transfer
    public void Abort(IRelaySender? relay, string reason)
    {
        TryClearReceiver(out ReceiverTransfer? receiver, out IRelaySender? recvRelay);
        TryCancelSend(out var sendTargetHost, out var sendRelay);

        var effectiveSendRelay = relay ?? sendRelay;
        if (effectiveSendRelay != null && sendTargetHost != null)
            SendTo(effectiveSendRelay, sendTargetHost, MessageKind.FileTransferAbort, new FileTransferAbortMessage(reason));

        if (receiver != null)
        {
            var effectiveRecvRelay = relay ?? recvRelay;
            if (effectiveRecvRelay != null)
                SendTo(effectiveRecvRelay, receiver.SourceHost, MessageKind.FileTransferAbort, new FileTransferAbortMessage(reason));
            CleanupReceiver(receiver);
        }

        _dialog.Close();
    }

    public async Task OnMessageAsync(string sourceHost, MessageKind kind, string json, IRelaySender relay)
    {
        switch (kind)
        {
            case MessageKind.FileTransferStart: HandleFileTransferStart(sourceHost, json, relay); break;
            case MessageKind.FileTransferChunk: await HandleFileTransferChunkAsync(sourceHost, json); break;
            case MessageKind.FileTransferDone: await HandleFileTransferDoneAsync(sourceHost, json, relay); break;
            case MessageKind.FileTransferAbort: HandleFileTransferAbort(sourceHost, json); break;
            case MessageKind.FileTransferAccepted:
                {
                    TaskCompletionSource<bool>? tcs;
                    lock (_lock) { tcs = _sendAcceptTcs; _sendAcceptTcs = null; }
                    tcs?.TrySetResult(true);
                    FirePasteOsd("Pasted!");
                    break;
                }
        }
    }

    private void HandleFileTransferStart(string sourceHost, string json, IRelaySender relay)
    {
        var msg = json.FromSaneJson<FileTransferStartMessage>();
        if (msg == null) { _log.LogWarning("Failed to deserialize FileTransferStart from {Host}", sourceHost); return; }

        // resolve destination before committing the receiver slot
        var destFolder = _dropTargetResolver.GetPasteDirectory();
        if (string.IsNullOrEmpty(destFolder))
        {
            SendTo(relay, sourceHost, MessageKind.FileTransferAbort, new FileTransferAbortMessage("no folder to paste into"));
            _dialog.ShowError("No paste destination — open a Finder/Explorer folder window first");
            _log.LogWarning("Transfer from {Host}: no valid paste destination — no file manager window is active", sourceHost);
            return;
        }
        _log.LogInformation("Paste destination: {Dest}", destFolder);

        // create (or replace) receiver — the message carries all metadata
        var newReceiver = new ReceiverTransfer(sourceHost, msg.FileNames ?? [], msg.TotalBytes, _watchdogTimeoutMs);
        ReceiverTransfer? existing;
        lock (_lock) { existing = _receiver; _receiver = newReceiver; _recvRelay = relay; }
        if (existing != null) CleanupReceiver(existing);
        var tempDir = TransferTempDir();
        CleanupTempDir(tempDir);
        var cts = newReceiver.Cts;
        cts.Token.Register(() => CleanupTempDir(tempDir));

        var extractor = new TarGzExtractor(tempDir, cts.Token);
        lock (_lock)
        {
            if (_receiver != newReceiver) { extractor.Dispose(); return; }
            var recv = _receiver!;
            recv.Extractor = extractor;
            recv.TempDir = tempDir;
            recv.DestFolder = destFolder;
            recv.TransferStartTick = Environment.TickCount64;
            recv.TouchWatchdog();
            _recvRelay = relay;
            // watchdog created inside the lock so Abort() can't race past it
            var watchdogRelay = relay;
            recv.Watchdog = new Timer(_ =>
            {
                if (newReceiver.WatchdogExpired())
                    AbortReceive(watchdogRelay, newReceiver, "transfer timed out");
            }, null, TimeSpan.FromMilliseconds(_watchdogTimeoutMs), TimeSpan.FromMilliseconds(_watchdogTimeoutMs));
        }

        // ack to sender: destination is valid, ready for chunks
        SendTo(relay, sourceHost, MessageKind.FileTransferAccepted, new FileTransferAcceptedMessage());
        _dialog.ShowTransferring(newReceiver.ToTransferInfo());
        _log.LogInformation("Transfer start from {Host}: {Count} file(s)", sourceHost, msg.FileNames?.Length ?? 0);
    }

    private async Task HandleFileTransferChunkAsync(string sourceHost, string json)
    {
        var chunk = json.FromSaneJson<FileTransferChunkMessage>();
        if (chunk == null) { _log.LogWarning("Failed to deserialize FileTransferChunk from {Host}", sourceHost); return; }
        var (sequence, data) = (chunk.Sequence, chunk.Data);
        ReceiverTransfer? receiver;
        lock (_lock) receiver = _receiver;
        if (receiver?.Extractor == null) return;
        if (!receiver.SourceHost.EqualsIgnoreCase(sourceHost)) return;

        try { await receiver.Extractor.WriteChunkAsync(data); }
        catch (Exception e) when (e is InvalidOperationException or ObjectDisposedException) { return; } // pipe completed (cleanup raced with this chunk)
        receiver.TouchWatchdog();
        var extracted = receiver.Extractor.BytesExtracted;
        var speed = CalcSpeed(receiver.TransferStartTick, receiver.Extractor.BytesReceived);
        _dialog.UpdateProgress(extracted, speed);
        _log.LogDebug("Chunk #{Seq} from {Host}: {Bytes} bytes", sequence, sourceHost, data.Length);
    }

    private async Task HandleFileTransferDoneAsync(string sourceHost, string json, IRelaySender relay)
    {
        var msg = json.FromSaneJson<FileTransferDoneMessage>();
        ReceiverTransfer? receiver;
        lock (_lock)
        {
            receiver = _receiver;
            if (_receiver != null && _receiver.SourceHost.EqualsIgnoreCase(sourceHost)) { _receiver = null; _recvRelay = null; }
            else receiver = null;
        }
        if (msg == null) { _log.LogWarning("Failed to deserialize FileTransferDone from {Host}", sourceHost); return; }
        if (receiver?.Extractor == null) return;

        await FinalizeReceivingAsync(receiver, msg, relay);
    }

    private void HandleFileTransferAbort(string sourceHost, string json)
    {
        var msg = json.FromSaneJson<FileTransferAbortMessage>();
        if (msg == null) _log.LogWarning("Failed to deserialize FileTransferAbort from {Host}", sourceHost);

        // ignore aborts from hosts we're not actively transferring with
        bool relevant;
        lock (_lock) relevant =
            (_sendTargetHost != null && _sendTargetHost.EqualsIgnoreCase(sourceHost)) ||
            (_receiver != null && _receiver.SourceHost.EqualsIgnoreCase(sourceHost));
        if (!relevant)
        {
            _log.LogDebug("FileTransferAbort from unexpected host {Host} — ignoring", sourceHost);
            return;
        }

        // cancel active send if we're the sender (receiver cancelled)
        if (TryCancelSend(out _, out _))
        {
            if (msg?.Reason == "no folder to paste into")
                FirePasteOsd("Invalid paste target");
            else
                ClearPasteOsd();
            _log.LogInformation("Transfer send cancelled by {Host}: {Reason}", sourceHost, msg?.Reason);
            _dialog.Close();
            return;
        }
        // otherwise cancel active receive if we're the receiver (sender aborted)
        TryClearReceiver(out var receiver, out _);
        if (receiver == null) return;
        CleanupReceiver(receiver);
        _dialog.ShowError($"Transfer aborted: {msg?.Reason}");
        _log.LogWarning("Transfer aborted by {Host}: {Reason}", sourceHost, msg?.Reason);
    }

    // -- private helpers --

    private void HandleCancelRequested()
    {
        // try aborting an active send first
        if (TryCancelSend(out var targetHost, out var relay))
        {
            ClearPasteOsd();
            if (targetHost != null && relay != null)
                SendTo(relay, targetHost, MessageKind.FileTransferAbort, new FileTransferAbortMessage("user cancelled"));
            _dialog.Close();
            _log.LogInformation("Transfer send cancelled by user");
            return;
        }

        // try aborting an active receive
        TryClearReceiver(out var receiver, out var recvRelay);
        if (receiver == null) return;

        // clean up locally — we already nulled _receiver above, so AbortReceive wouldn't find it
        CleanupReceiver(receiver);
        _dialog.Close();

        var effectiveRelay = recvRelay;
        if (effectiveRelay != null)
        {
            SendTo(effectiveRelay, receiver.SourceHost, MessageKind.FileTransferAbort, new FileTransferAbortMessage("user cancelled"));
            _log.LogInformation("Transfer receive cancelled by user");
        }
        else
            _log.LogInformation("Transfer receive cancelled by user (pending — no relay yet)");
    }

    // orchestrates a paste: source→target file transfer, handling all three host-topology cases.
    // localHost is the name of the local (master) machine.
    // osd, if provided, is called exactly once with "Pasted!" on success or "Invalid paste target" on rejection.
    public void InitiatePaste(FileCopyState copyBuffer, string targetHost, string localHost, IRelaySender relay, Action<string>? osd = null)
    {
        lock (_lock) _pasteOsd = osd;

        var paths = copyBuffer.Paths.ToList();
        if (string.Equals(copyBuffer.SourceHost, localHost, StringComparison.OrdinalIgnoreCase))
        {
            // case 1: source is local master → stream directly to target; OSD fired on first chunk or abort
            StartSend(paths, targetHost, relay);
        }
        else
        {
            // cases 2 & 3: source is a remote slave → tell it to stream to targetHost.
            // no feedback path to master on target rejection, so fire "Pasted!" optimistically now.
            FirePasteOsd("Pasted!");
            var req = new FileStreamRequestMessage(copyBuffer.Paths, targetHost);
            SendTo(relay, copyBuffer.SourceHost, MessageKind.FileStreamRequest, req);
            _log.LogInformation("Paste: requested {SourceHost} to stream {Count} file(s) → {TargetHost}",
                copyBuffer.SourceHost, copyBuffer.Paths.Length, targetHost);
        }
    }

    // streams files as tar.gz to targetHost. called on the slave side in response to FileStreamRequest.
    public async Task StreamToHost(string[] paths, string targetHost, IRelaySender relay)
    {
        var cts = new CancellationTokenSource();
        lock (_lock)
        {
            if (_sendCts != null || _receiver != null) { cts.Dispose(); return; }
            _sendCts = cts;
            _sendTargetHost = targetHost;
            _sendRelay = relay;
        }

        List<string> pathList;
        long totalBytes;
        string[] names;
        try
        {
            pathList = [.. paths];
            totalBytes = TarGzStreamer.ComputeTotalBytes(pathList);
            names = [.. pathList.Select(Path.GetFileName).Where(n => n != null).Cast<string>()];
        }
        catch (Exception ex)
        {
            TryCancelSend(out _, out _);
            SendTo(relay, targetHost, MessageKind.FileTransferAbort, new FileTransferAbortMessage(ex.Message));
            _dialog.ShowError($"Transfer failed: {ex.Message}");
            _log.LogWarning(ex, "StreamToHost pre-stream setup failed");
            return;
        }

        var startTick = Environment.TickCount64;
        _dialog.ShowTransferring(new FileTransferInfo(names, totalBytes, IsSender: true));
        await StreamAsync(pathList, names, totalBytes, startTick, targetHost, relay, cts.Token);
    }

    // starts an outbound transfer to targetHost. called on the sender side.
    public void StartSend(List<string> paths, string targetHost, IRelaySender relay)
    {
        var cts = new CancellationTokenSource();
        lock (_lock)
        {
            if (_sendCts != null || _receiver != null) { cts.Dispose(); return; }
            _sendCts = cts;
            _sendTargetHost = targetHost;
            _sendRelay = relay;
            _sendAcceptTcs = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        }

        long totalBytes;
        string[] names;
        try
        {
            totalBytes = TarGzStreamer.ComputeTotalBytes(paths);
            names = [.. paths.Select(Path.GetFileName).Where(n => n != null).Cast<string>()];
        }
        catch (Exception ex)
        {
            TryCancelSend(out _, out _);
            _log.LogWarning(ex, "StartSend pre-stream setup failed");
            _dialog.ShowError($"Transfer failed: {ex.Message}");
            return;
        }

        var tick = Environment.TickCount64;
        _dialog.ShowTransferring(new FileTransferInfo(names, totalBytes, IsSender: true));
        _ = Task.Run(() => StreamAsync(paths, names, totalBytes, tick, targetHost, relay, cts.Token));
    }

    private async Task StreamAsync(List<string> paths, string[] names, long totalBytes, long startTick, string targetHost, IRelaySender relay, CancellationToken cancel)
    {
        long totalSent = 0;
        try
        {
            var startPayload = MessageSerializer.Encode(MessageKind.FileTransferStart, new FileTransferStartMessage(names, totalBytes));
            await relay.Send([targetHost], startPayload);

            // wait for receiver to validate the paste destination before streaming
            TaskCompletionSource<bool>? acceptTcs;
            lock (_lock) acceptTcs = _sendAcceptTcs;
            if (acceptTcs != null) await acceptTcs.Task.WaitAsync(TimeSpan.FromMilliseconds(_watchdogTimeoutMs), cancel);

            var sha = await TarGzStreamer.StreamAsync(paths, async (data, seq, uncompressedBytes) =>
            {
                cancel.ThrowIfCancellationRequested();
                var chunkPayload = MessageSerializer.Encode(MessageKind.FileTransferChunk, new FileTransferChunkMessage(seq, data));
                await relay.Send([targetHost], chunkPayload);
                totalSent += data.Length;
                var speed = CalcSpeed(startTick, totalSent);
                _dialog.UpdateProgress(uncompressedBytes, speed);
                _log.LogDebug("Sent chunk #{Seq}: {Bytes} bytes", seq, data.Length);
            }, cancel);

            var donePayload = MessageSerializer.Encode(MessageKind.FileTransferDone, new FileTransferDoneMessage(totalSent, sha));
            await relay.Send([targetHost], donePayload);
            _dialog.ShowCompleted();
            _log.LogInformation("Transfer complete: {Bytes} compressed bytes sent", ByteSize.FromBytes(totalSent));
        }
        catch (OperationCanceledException) when (cancel.IsCancellationRequested)
        {
            _log.LogInformation("Transfer cancelled");
            _dialog.Close();
        }
        catch (TimeoutException)
        {
            _log.LogWarning("Transfer timed out waiting for {Target} to respond", targetHost);
            SendTo(relay, targetHost, MessageKind.FileTransferAbort, new FileTransferAbortMessage("transfer timed out"));
            _dialog.ShowError("Transfer failed: destination did not respond");
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "Transfer failed");
            SendTo(relay, targetHost, MessageKind.FileTransferAbort, new FileTransferAbortMessage(ex.Message));
            _dialog.ShowError($"Transfer failed: {ex.Message}");
        }
        finally
        {
            lock (_lock)
            {
                _sendCts?.Dispose();
                _sendCts = null;
                _sendTargetHost = null;
                _sendRelay = null;
                _pasteOsd = null;       // clear if not already fired (e.g. unexpected exception)
                _sendAcceptTcs = null;  // clear if not already resolved/cancelled
            }
        }
    }

    private async Task FinalizeReceivingAsync(ReceiverTransfer receiver, FileTransferDoneMessage msg, IRelaySender relay)
    {
        try
        {
            await receiver.Extractor!.CompleteAsync();
            var actualHash = receiver.Extractor.GetHash();
            var bytesReceived = receiver.Extractor.BytesReceived;
            var hashMatch = actualHash.SequenceEqual(msg.Sha256);

            if (bytesReceived != msg.TotalBytesSent || !hashMatch)
            {
                CleanupReceiver(receiver);
                _dialog.ShowError("Transfer failed: data integrity check failed");
                _log.LogWarning("Integrity failure from {Host} (received={Received}, expected={Expected}, hashMatch={HashMatch})",
                    receiver.SourceHost, bytesReceived, msg.TotalBytesSent, hashMatch);
                return;
            }

            // move files out before CleanupReceiver deletes the temp dir
            _dropTargetResolver.MoveToDestination(receiver.TempDir!, receiver.DestFolder!);
            CleanupReceiver(receiver);
            _dialog.ShowCompleted();
            _log.LogInformation("Transfer complete: files in {Dest}", receiver.DestFolder);
        }
        catch (Exception ex)
        {
            CleanupReceiver(receiver);
            SendTo(relay, receiver.SourceHost, MessageKind.FileTransferAbort, new FileTransferAbortMessage(ex.Message));
            _dialog.ShowError($"Transfer failed: {ex.Message}");
            _log.LogWarning(ex, "Failed to finalize transfer from {Host}", receiver.SourceHost);
        }
    }

    private void AbortReceive(IRelaySender relay, ReceiverTransfer expected, string reason)
    {
        lock (_lock)
        {
            if (_receiver != expected) return;
            _receiver = null;
            _recvRelay = null;
        }
        // fromWatchdog=true: we are inside the watchdog callback — don't block waiting for it to finish
        CleanupReceiver(expected, fromWatchdog: true);
        SendTo(relay, expected.SourceHost, MessageKind.FileTransferAbort, new FileTransferAbortMessage(reason));
        _dialog.ShowError($"Transfer aborted: {reason}");
    }

    private static void CleanupReceiver(ReceiverTransfer receiver, bool fromWatchdog = false)
    {
        if (receiver.Watchdog is { } watchdog)
        {
            if (fromWatchdog)
                // we ARE the watchdog callback — just dispose, don't wait for ourselves
                watchdog.Dispose();
            else
            {
                // wait for any in-flight watchdog callback to complete before disposing the CTS
                using var done = new ManualResetEvent(false);
                watchdog.Dispose(done);
                done.WaitOne(WatchdogDisposalWaitMs);
            }
        }
        receiver.Dispose();
        if (receiver.TempDir != null)
            CleanupTempDir(receiver.TempDir);
    }

    // encodes a message and fire-and-forgets the send, logging on failure
    private void SendTo(IRelaySender relay, string host, MessageKind kind, object msg)
    {
        var payload = MessageSerializer.Encode(kind, msg);
        relay.Send([host], payload).AsTask()
            .ContinueWith(t => _log.LogWarning(t.Exception, "Relay send failed"), TaskContinuationOptions.OnlyOnFaulted);
    }

    private static void CleanupTempDir(string tempDir)
    {
        try { if (Directory.Exists(tempDir)) Directory.Delete(tempDir, recursive: true); }
        catch { /* best effort */ }
    }

    // intentional fixed path: FileTransferOngoing prevents concurrent transfers, and reusing the
    // same directory ensures any previous temp content is cleaned up before the next transfer begins.
    private static string TransferTempDir() => Path.Combine(Path.GetTempPath(), "hydra", "transfer");

    public void Dispose()
    {
        _dialog.CancelRequested -= HandleCancelRequested;
        Abort(relay: null, "service stopped");
    }

    public sealed record FileCopyState(string SourceHost, string[] Paths);

    // lifecycle:
    //   created on FileTransferStart  → SourceHost/FileNames/TotalBytes/Cts set
    //   advanced on FileTransferStart → Extractor/TempDir/DestFolder/TransferStartTick/Watchdog set
    //   finalised on FileTransferDone → FinalizeReceivingAsync runs, then CleanupReceiver
    // all nullable fields are null until the corresponding phase is reached
    private sealed class ReceiverTransfer(string sourceHost, string[] fileNames, long totalBytes, long watchdogTimeoutMs) : IDisposable
    {
        private long _lastChunkTick = Environment.TickCount64;

        public string SourceHost { get; } = sourceHost;
        public string[] FileNames { get; } = fileNames;
        public long TotalBytes { get; } = totalBytes;
        public TarGzExtractor? Extractor { get; set; }   // set in FileTransferStart phase
        public string? TempDir { get; set; }              // set in FileTransferStart phase
        public string? DestFolder { get; set; }           // set in FileTransferStart phase
        public long TransferStartTick { get; set; }       // set in FileTransferStart phase
        public Timer? Watchdog { get; set; }              // set in FileTransferStart phase
        public CancellationTokenSource Cts { get; } = new();

        public void TouchWatchdog() => Interlocked.Exchange(ref _lastChunkTick, Environment.TickCount64);
        public bool WatchdogExpired() => Environment.TickCount64 - Interlocked.Read(ref _lastChunkTick) > watchdogTimeoutMs;
        public FileTransferInfo ToTransferInfo() => new(FileNames, TotalBytes, IsSender: false);

        public void Dispose()
        {
            Cts.Cancel();
            Extractor?.Dispose();
            Cts.Dispose();
        }
    }
}

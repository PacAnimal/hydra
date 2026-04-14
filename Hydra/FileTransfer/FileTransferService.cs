using ByteSizeLib;
using Cathedral.Extensions;
using Hydra.Relay;
using Hydra.Screen;
using Microsoft.Extensions.Logging;

namespace Hydra.FileTransfer;

public sealed class FileTransferService : IDisposable
{
    private readonly IFileDragSource _dragSource;
    private readonly IFileTransferDialog _dialog;
    private readonly IDropTargetResolver _dropTargetResolver;
    private readonly ILogger<FileTransferService> _log;
    private readonly Lock _lock = new();

    private const double SpeedMinElapsedSec = 0.5;  // avoid nonsense speed values in the first half-second
    private const int WatchdogDisposalWaitMs = 1000;  // how long to wait for in-flight watchdog callback on cleanup

    private readonly int _watchdogTimeoutMs;

    // sender side
    private SenderDrag? _drag;               // set during DragReady phase (mouse held, not yet released)
    private CancellationTokenSource? _sendCts;
    private string? _sendTargetHost;
    private IRelaySender? _sendRelay;

    // receiver side
    private ReceiverTransfer? _receiver;
    private IRelaySender? _recvRelay;

    public bool IsDragReady { get { lock (_lock) return _drag != null; } }

    // true while any transfer is in flight (drag pending, sending, or receiving) — used to block new drags
    public bool FileTransferOngoing { get { lock (_lock) return _drag != null || _sendCts != null || _receiver != null; } }

    public void UpdateActiveEdges(List<ActiveEdgeRange> ranges) => _dragSource.UpdateActiveEdges(ranges);

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
        }
        if (cts == null) return false;
        cts.Cancel();
        cts.Dispose();
        return true;
    }

    public static bool IsFileTransferMessage(MessageKind kind) => kind is
        MessageKind.FileDragEnter or MessageKind.FileDragCancel or
        MessageKind.FileTransferStart or MessageKind.FileTransferChunk or
        MessageKind.FileTransferDone or MessageKind.FileTransferAbort;

    internal static FileTransferService Null() =>
        new(new NullFileDragSource(), new NullFileTransferDialog(), new NullDropTargetResolver(), Microsoft.Extensions.Logging.Abstractions.NullLogger<FileTransferService>.Instance);

    public FileTransferService(IFileDragSource dragSource, IFileTransferDialog dialog, IDropTargetResolver dropTargetResolver, ILogger<FileTransferService> log, int watchdogTimeoutMs = 30_000)
    {
        _dragSource = dragSource;
        _dialog = dialog;
        _dropTargetResolver = dropTargetResolver;
        _log = log;
        _watchdogTimeoutMs = watchdogTimeoutMs;
        // subscribe once — reads current state on each cancel click
        dialog.CancelRequested += HandleCancelRequested;
    }

    // called by InputRouter when left button is held during edge crossing
    public bool TryBeginDrag(string targetHost, IRelaySender relay)
    {
        if (FileTransferOngoing) return false;

        _log.LogDebug("TryBeginDrag: querying drag source for {Host}", targetHost);
        var paths = _dragSource.GetDraggedPaths();
        if (paths == null || paths.Count == 0)
        {
            _log.LogDebug("TryBeginDrag: no dragged paths detected");
            return false;
        }

        // NOTE: ComputeTotalBytes enumerates all files synchronously — for large directory trees this
        // briefly blocks the input thread. Acceptable in practice; deferred async sizing would require
        // a second message to update the receiver's TotalBytes.
        var totalBytes = TarGzStreamer.ComputeTotalBytes(paths);
        var names = paths.Select(Path.GetFileName).Where(n => n != null).Cast<string>().ToArray();
        var drag = new SenderDrag(targetHost, paths, names, totalBytes);

        lock (_lock) _drag = drag;

        SendTo(relay, targetHost, MessageKind.FileDragEnter, new FileDragEnterMessage(names, totalBytes));
        _dialog.ShowPending(drag.ToTransferInfo());
        _log.LogInformation("Drag enter: {Count} item(s), {Bytes} bytes → {Host}", names.Length, ByteSize.FromBytes(totalBytes), targetHost);
        return true;
    }

    // called when cursor crosses from one remote host to another while button is still held
    public void ReTargetDrag(string newHost, IRelaySender relay)
    {
        SenderDrag? oldDrag;
        lock (_lock)
        {
            oldDrag = _drag;
            if (oldDrag == null) return;
            _drag = new SenderDrag(newHost, oldDrag.Paths, oldDrag.FileNames, oldDrag.TotalBytes);
        }

        // cancel on the old target (may be Linux/non-transfer — it will just ignore it)
        SendTo(relay, oldDrag.TargetHost, MessageKind.FileDragCancel, new FileDragCancelMessage());
        SendTo(relay, newHost, MessageKind.FileDragEnter, new FileDragEnterMessage(oldDrag.FileNames, oldDrag.TotalBytes));
        _dialog.ShowPending(oldDrag.ToTransferInfo());
        _log.LogInformation("Drag retargeted: {Count} item(s) → {Host}", oldDrag.FileNames.Length, newHost);
    }

    // called by InputRouter when drag returns to source screen before release
    public void CancelDrag(IRelaySender relay)
    {
        SenderDrag? drag;
        lock (_lock) { drag = _drag; _drag = null; }
        if (drag == null) return;

        SendTo(relay, drag.TargetHost, MessageKind.FileDragCancel, new FileDragCancelMessage());
        _dialog.Close();
        _log.LogInformation("Drag cancelled");
    }

    // called by InputRouter when left button released on remote screen
    public void Drop(IRelaySender relay)
    {
        SenderDrag? drag;
        var cts = new CancellationTokenSource();
        lock (_lock)
        {
            drag = _drag; _drag = null;
            if (drag == null) { cts.Dispose(); return; }
            drag.TransferStartTick = Environment.TickCount64;
            _sendCts = cts;
            _sendTargetHost = drag.TargetHost;
            _sendRelay = relay;
        }

        _dialog.ShowTransferring(drag.ToTransferInfo());

        // StreamAsync sends FileTransferStart before any chunks, ensuring ordering
        _ = Task.Run(() => StreamAsync(drag, relay, cts.Token));
    }

    // called when relay disconnects or peer departs during an active transfer
    public void Abort(IRelaySender? relay, string reason)
    {
        SenderDrag? drag;

        lock (_lock) { drag = _drag; _drag = null; }
        TryClearReceiver(out ReceiverTransfer? receiver, out IRelaySender? recvRelay);
        TryCancelSend(out var sendTargetHost, out var sendRelay);

        // prefer the caller's relay; fall back to stored relays (e.g. Dispose() passes null)
        var effectiveSendRelay = relay ?? sendRelay;
        if (effectiveSendRelay != null)
        {
            if (sendTargetHost != null)
                // active send — abort with reason
                SendTo(effectiveSendRelay, sendTargetHost, MessageKind.FileTransferAbort, new FileTransferAbortMessage(reason));
            else if (drag != null)
                // drag-only phase (before drop) — send cancel, not abort
                SendTo(effectiveSendRelay, drag.TargetHost, MessageKind.FileDragCancel, new FileDragCancelMessage());
        }

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
            case MessageKind.FileDragEnter: HandleFileDragEnter(sourceHost, json, relay); break;
            case MessageKind.FileDragCancel: HandleFileDragCancel(sourceHost); break;
            case MessageKind.FileTransferStart: HandleFileTransferStart(sourceHost, relay); break;
            case MessageKind.FileTransferChunk: await HandleFileTransferChunkAsync(sourceHost, json); break;
            case MessageKind.FileTransferDone: await HandleFileTransferDoneAsync(sourceHost, json, relay); break;
            case MessageKind.FileTransferAbort: HandleFileTransferAbort(sourceHost, json); break;
        }
    }

    private void HandleFileDragEnter(string sourceHost, string json, IRelaySender relay)
    {
        var msg = json.FromSaneJson<FileDragEnterMessage>();
        if (msg == null) { _log.LogWarning("Failed to deserialize FileDragEnter from {Host}", sourceHost); return; }
        var newReceiver = new ReceiverTransfer(sourceHost, msg.FileNames, msg.TotalBytes, _watchdogTimeoutMs);
        ReceiverTransfer? existing;
        lock (_lock) { existing = _receiver; _receiver = newReceiver; _recvRelay = relay; }
        if (existing != null) CleanupReceiver(existing);
        _dialog.ShowPending(newReceiver.ToTransferInfo());
        _log.LogInformation("Drag enter from {Host}: {Count} item(s), {Bytes}", sourceHost, msg.FileNames.Length, ByteSize.FromBytes(msg.TotalBytes));
    }

    private void HandleFileDragCancel(string sourceHost)
    {
        TryClearReceiver(out var receiver, out _);
        if (receiver != null) CleanupReceiver(receiver);
        _dialog.Close();
        _log.LogInformation("Drag cancelled by {Host}", sourceHost);
    }

    private void HandleFileTransferStart(string sourceHost, IRelaySender relay)
    {
        ReceiverTransfer? receiver;
        lock (_lock) receiver = _receiver;
        if (receiver == null) return;

        var destFolder = _dropTargetResolver.GetDirectoryUnderCursor() ?? FallbackFolder();
        var tempDir = TransferTempDir();
        CleanupTempDir(tempDir);
        var cts = receiver.Cts;
        cts.Token.Register(() => CleanupTempDir(tempDir));

        var extractor = new TarGzExtractor(tempDir, cts.Token);
        lock (_lock)
        {
            if (_receiver != receiver) { extractor.Dispose(); return; }
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
                if (receiver.WatchdogExpired())
                    AbortReceive(watchdogRelay, receiver, "transfer timed out");
            }, null, TimeSpan.FromMilliseconds(_watchdogTimeoutMs), TimeSpan.FromMilliseconds(_watchdogTimeoutMs));
        }

        _dialog.ShowTransferring(receiver.ToTransferInfo());
        _log.LogInformation("Transfer start from {Host}", sourceHost);
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

    private async Task StreamAsync(SenderDrag drag, IRelaySender relay, CancellationToken cancel)
    {
        long totalSent = 0;
        try
        {
            // send start before any chunks so the receiver always processes them in order
            var startPayload = MessageSerializer.Encode(MessageKind.FileTransferStart, new FileTransferStartMessage());
            await relay.Send([drag.TargetHost], startPayload);

            var sha = await TarGzStreamer.StreamAsync(drag.Paths, async (data, seq, uncompressedBytes) =>
            {
                cancel.ThrowIfCancellationRequested();
                var chunkPayload = MessageSerializer.Encode(MessageKind.FileTransferChunk, new FileTransferChunkMessage(seq, data));
                await relay.Send([drag.TargetHost], chunkPayload);
                totalSent += data.Length;
                var speed = CalcSpeed(drag.TransferStartTick, totalSent);
                _dialog.UpdateProgress(uncompressedBytes, speed);
                _log.LogDebug("Sent chunk #{Seq}: {Bytes} bytes", seq, data.Length);
            }, cancel);

            var donePayload = MessageSerializer.Encode(MessageKind.FileTransferDone, new FileTransferDoneMessage(totalSent, sha));
            await relay.Send([drag.TargetHost], donePayload);
            _dialog.ShowCompleted();
            _log.LogInformation("Transfer complete: {Bytes} compressed bytes sent", ByteSize.FromBytes(totalSent));
        }
        catch (OperationCanceledException) when (cancel.IsCancellationRequested)
        {
            _log.LogInformation("Transfer cancelled");
            _dialog.Close();
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "Transfer failed");
            SendTo(relay, drag.TargetHost, MessageKind.FileTransferAbort, new FileTransferAbortMessage(ex.Message));
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

    // fallback when cursor is not over a recognisable folder view
    private static string FallbackFolder() =>
        Environment.GetFolderPath(Environment.SpecialFolder.Desktop);

    // intentional fixed path: FileTransferOngoing prevents concurrent transfers, and reusing the
    // same directory ensures any previous temp content is cleaned up before the next transfer begins.
    private static string TransferTempDir() => Path.Combine(Path.GetTempPath(), "hydra", "transfer");

    public void Dispose()
    {
        _dialog.CancelRequested -= HandleCancelRequested;
        Abort(relay: null, "service stopped");
    }

    // -- state records --

    private sealed class SenderDrag(string targetHost, List<string> paths, string[] fileNames, long totalBytes)
    {
        public string TargetHost { get; } = targetHost;
        public List<string> Paths { get; } = paths;
        public string[] FileNames { get; } = fileNames;
        public long TotalBytes { get; } = totalBytes;
        public long TransferStartTick { get; set; }
        public FileTransferInfo ToTransferInfo() => new(FileNames, TotalBytes, IsSender: true);
    }

    // lifecycle:
    //   created on FileDragEnter  → SourceHost/FileNames/TotalBytes/Cts set
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

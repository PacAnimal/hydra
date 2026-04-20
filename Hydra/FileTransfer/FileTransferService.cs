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

    // abort reason sent when the receiver has no file manager window open — checked by master for OSD routing
    public const string ReasonNoFolder = "no folder to paste into";

    // copy buffer (set on copy hotkey; consumed on paste hotkey)
    private FileCopyState? _copyBuffer;

    // sender side (case 1: master → slave)
    private CancellationTokenSource? _sendCts;
    private string? _sendTargetHost;
    private IRelaySender? _sendRelay;
    private TaskCompletionSource<bool>? _sendAcceptTcs;  // completed when receiver sends FileTransferAccepted

    // coordinator state (case 3: slave → slave; master negotiates then hands off)
    private string? _coordTargetHost;
    private string? _coordSourceHost;
    private string[]? _coordSourcePaths;
    private IRelaySender? _coordRelay;

    // receiver side
    private ReceiverTransfer? _receiver;
    private IRelaySender? _recvRelay;

    // true while any transfer is in flight (sending, receiving, or coordinating)
    public bool FileTransferOngoing { get { lock (_lock) return _sendCts != null || _receiver != null || _coordTargetHost != null; } }

    // true if we are currently sending to the given host (case 1)
    public bool IsSendingTo(string host) { lock (_lock) return _sendTargetHost != null && _sendTargetHost.EqualsIgnoreCase(host); }

    // true if we are currently receiving from the given host (case 2: master is target)
    public bool IsReceivingFrom(string host) { lock (_lock) return _receiver != null && _receiver.SourceHost.EqualsIgnoreCase(host); }

    // true if we are coordinating a slave→slave transfer that the given target host accepted/aborted (case 3)
    public bool IsCoordinatingTransferTo(string targetHost) { lock (_lock) return _coordTargetHost != null && _coordTargetHost.EqualsIgnoreCase(targetHost); }

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
        return "0 items selected";
    }

    public FileCopyState? GetCopyBuffer() { lock (_lock) return _copyBuffer; }
    public void ClearCopyBuffer() { lock (_lock) _copyBuffer = null; }

    private static double CalcSpeed(long startTick, long bytes)
    {
        var elapsed = (Environment.TickCount64 - startTick) / 1000.0;
        return elapsed > SpeedMinElapsedSec ? bytes / elapsed : 0;
    }

    private void TryClearReceiver(out ReceiverTransfer? receiver, out IRelaySender? recvRelay)
    {
        lock (_lock)
        {
            receiver = _receiver; _receiver = null;
            recvRelay = _recvRelay; _recvRelay = null;
        }
    }

    // swaps out _sendCts under lock, cancels and disposes it; also clears host/relay/tcs fields.
    // returns true if there was an active send to cancel.
    private bool TryCancelSend(out string? targetHost, out IRelaySender? sendRelay)
    {
        CancellationTokenSource? cts;
        lock (_lock)
        {
            cts = _sendCts; _sendCts = null;
            targetHost = _sendTargetHost; _sendTargetHost = null;
            sendRelay = _sendRelay; _sendRelay = null;
            _sendAcceptTcs = null;
        }
        if (cts == null) return false;
        cts.Cancel();
        cts.Dispose();
        return true;
    }

    // clears coordinator state and returns the target host + relay (null if not coordinating)
    private (string? target, IRelaySender? relay) TryClearCoordinator()
    {
        lock (_lock)
        {
            var target = _coordTargetHost;
            var relay = _coordRelay;
            _coordTargetHost = null;
            _coordSourceHost = null;
            _coordSourcePaths = null;
            _coordRelay = null;
            return (target, relay);
        }
    }

    public static bool IsFileTransferMessage(MessageKind kind) => kind is
        MessageKind.FileTransferRequest or MessageKind.FileTransferStart or
        MessageKind.FileTransferChunk or MessageKind.FileTransferDone or
        MessageKind.FileTransferAbort or MessageKind.FileTransferAccepted;

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
        var (coordTarget, coordRelay) = TryClearCoordinator();

        var effectiveSendRelay = relay ?? sendRelay;
        if (effectiveSendRelay != null && sendTargetHost != null)
            SendTo(effectiveSendRelay, sendTargetHost, MessageKind.FileTransferAbort, new FileTransferAbortMessage(reason));

        var effectiveCoordRelay = relay ?? coordRelay;
        if (effectiveCoordRelay != null && coordTarget != null)
            SendTo(effectiveCoordRelay, coordTarget, MessageKind.FileTransferAbort, new FileTransferAbortMessage(reason));

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
            case MessageKind.FileTransferRequest: HandleFileTransferRequest(sourceHost, json, relay); break;
            case MessageKind.FileTransferStart: HandleFileTransferStart(sourceHost, json); break;
            case MessageKind.FileTransferChunk: await HandleFileTransferChunkAsync(sourceHost, json); break;
            case MessageKind.FileTransferDone: await HandleFileTransferDoneAsync(sourceHost, json, relay); break;
            case MessageKind.FileTransferAbort: HandleFileTransferAbort(sourceHost, json); break;
            case MessageKind.FileTransferAccepted:
                {
                    // case 1: master was sending — unblock StreamAsync to start chunk flow
                    TaskCompletionSource<bool>? tcs;
                    // case 3: master was coordinating — tell source slave to start streaming
                    string? coordSource; string[]? coordPaths; string? coordTarget;
                    lock (_lock)
                    {
                        tcs = _sendAcceptTcs; _sendAcceptTcs = null;
                        coordSource = _coordSourceHost; _coordSourceHost = null;
                        coordPaths = _coordSourcePaths; _coordSourcePaths = null;
                        coordTarget = _coordTargetHost; _coordTargetHost = null;
                        _coordRelay = null;
                    }
                    tcs?.TrySetResult(true);
                    if (coordSource != null && coordPaths != null && coordTarget != null)
                    {
                        var req = new FileStreamRequestMessage(coordPaths, coordTarget);
                        SendTo(relay, coordSource, MessageKind.FileStreamRequest, req);
                        _log.LogInformation("Target {Target} accepted — sending FileStreamRequest to {Source}", coordTarget, coordSource);
                    }
                    break;
                }
        }
    }

    private void HandleFileTransferRequest(string sourceHost, string json, IRelaySender relay)
    {
        var msg = json.FromSaneJson<FileTransferRequestMessage>();
        if (msg == null) { _log.LogWarning("Failed to deserialize FileTransferRequest from {Host}", sourceHost); return; }

        var destFolder = _dropTargetResolver.GetPasteDirectory();
        if (string.IsNullOrEmpty(destFolder))
        {
            SendTo(relay, sourceHost, MessageKind.FileTransferAbort, new FileTransferAbortMessage(ReasonNoFolder));
            _log.LogWarning("Transfer from {Host}: no valid paste destination — no file manager window is active", sourceHost);
            return;
        }
        _log.LogInformation("Paste destination: {Dest}", destFolder);

        var dataSourceHost = msg.SourceHost ?? sourceHost;
        SetupReceiverInternal(dataSourceHost, destFolder, relay);
        SendTo(relay, sourceHost, MessageKind.FileTransferAccepted, new FileTransferAcceptedMessage());
        _log.LogInformation("Transfer request from {Host}: data expected from {DataSource}", sourceHost, dataSourceHost);
    }

    private void HandleFileTransferStart(string sourceHost, string json)
    {
        var msg = json.FromSaneJson<FileTransferStartMessage>();
        if (msg == null) { _log.LogWarning("Failed to deserialize FileTransferStart from {Host}", sourceHost); return; }

        ReceiverTransfer? receiver;
        lock (_lock) receiver = _receiver;
        if (receiver == null || !receiver.SourceHost.EqualsIgnoreCase(sourceHost))
        {
            _log.LogWarning("FileTransferStart from unexpected host {Host} — no matching receiver", sourceHost);
            return;
        }

        receiver.FileNames = msg.FileNames;
        receiver.TotalBytes = msg.TotalBytes;
        _dialog.ShowTransferring(receiver.ToTransferInfo());
        _log.LogInformation("Transfer start from {Host}: {Count} file(s), {Total} bytes", sourceHost, msg.FileNames.Length, msg.TotalBytes);
    }

    private async Task HandleFileTransferChunkAsync(string sourceHost, string json)
    {
        var chunk = json.FromSaneJson<FileTransferChunkMessage>();
        if (chunk == null) { _log.LogWarning("Failed to deserialize FileTransferChunk from {Host}", sourceHost); return; }
        var (sequence, data) = (chunk.Sequence, chunk.Data);
        ReceiverTransfer? receiver;
        lock (_lock) receiver = _receiver;
        if (receiver?.Extractor == null) return;
        if (!receiver.SourceHost.EqualsIgnoreCase(sourceHost))
        {
            _log.LogError("FileTransferChunk from unexpected host {Host} (expected {Expected}) — dropping", sourceHost, receiver.SourceHost);
            return;
        }

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

        bool relevant;
        lock (_lock)
        {
            relevant =
                (_sendTargetHost != null && _sendTargetHost.EqualsIgnoreCase(sourceHost)) ||
                (_receiver != null && _receiver.SourceHost.EqualsIgnoreCase(sourceHost)) ||
                (_coordTargetHost != null && _coordTargetHost.EqualsIgnoreCase(sourceHost));

            // clear coordinator state if target aborted
            if (_coordTargetHost != null && _coordTargetHost.EqualsIgnoreCase(sourceHost))
            {
                _coordTargetHost = null;
                _coordSourceHost = null;
                _coordSourcePaths = null;
                _coordRelay = null;
            }
        }

        if (!relevant)
        {
            _log.LogDebug("FileTransferAbort from unexpected host {Host} — ignoring", sourceHost);
            return;
        }

        if (TryCancelSend(out _, out _))
        {
            _log.LogInformation("Transfer send cancelled by {Host}: {Reason}", sourceHost, msg?.Reason);
            _dialog.Close();
            return;
        }
        TryClearReceiver(out var receiver, out _);
        if (receiver == null) return;
        CleanupReceiver(receiver);
        _dialog.ShowError($"Transfer aborted: {msg?.Reason}");
        _log.LogWarning("Transfer aborted by {Host}: {Reason}", sourceHost, msg?.Reason);
    }

    // -- private helpers --

    private void HandleCancelRequested()
    {
        if (TryCancelSend(out var targetHost, out var relay))
        {
            if (targetHost != null && relay != null)
                SendTo(relay, targetHost, MessageKind.FileTransferAbort, new FileTransferAbortMessage("user cancelled"));
            _dialog.Close();
            _log.LogInformation("Transfer send cancelled by user");
            return;
        }

        TryClearReceiver(out var receiver, out var recvRelay);
        if (receiver == null) return;

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

    // orchestrates a paste; returns false if master is the target and has no valid paste directory.
    // localHost is the name of the local (master) machine.
    public bool InitiatePaste(FileCopyState copyBuffer, string targetHost, string localHost, IRelaySender relay)
    {
        var paths = copyBuffer.Paths;
        var sourceHost = copyBuffer.SourceHost;

        if (targetHost.EqualsIgnoreCase(localHost))
        {
            // case 2: master is target — validate paste dir locally, set up receiver, tell source to stream
            var destFolder = _dropTargetResolver.GetPasteDirectory();
            if (string.IsNullOrEmpty(destFolder))
            {
                _log.LogWarning("Paste: no valid paste destination on master");
                return false;
            }
            SetupReceiverInternal(sourceHost, destFolder, relay);
            var req = new FileStreamRequestMessage(paths, localHost);
            SendTo(relay, sourceHost, MessageKind.FileStreamRequest, req);
            _log.LogInformation("Paste: master as receiver from {Source} — told source to stream", sourceHost);
            return true;
        }

        if (sourceHost.EqualsIgnoreCase(localHost))
        {
            // case 1: master is source — send FileTransferRequest to target, await accepted, then stream
            StartSend([.. paths], targetHost, relay, localHost);
            return true;
        }

        // case 3: slave→slave — tell target to expect data from sourceHost; on accept, tell source to stream
        lock (_lock)
        {
            if (_sendCts != null || _receiver != null || _coordTargetHost != null)
            {
                _log.LogWarning("Paste: transfer already in progress");
                return true;
            }
            _coordTargetHost = targetHost;
            _coordSourceHost = sourceHost;
            _coordSourcePaths = paths;
            _coordRelay = relay;
        }
        SendTo(relay, targetHost, MessageKind.FileTransferRequest, new FileTransferRequestMessage(SourceHost: sourceHost));
        _log.LogInformation("Paste: sent FileTransferRequest to {Target} (data from {Source})", targetHost, sourceHost);
        return true;
    }

    // streams files as tar.gz to targetHost. called on the slave side in response to FileStreamRequest.
    // master has already sent FileTransferStart to target and received FileTransferAccepted — go straight to chunks.
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

        // inform target of what's coming before streaming
        SendTo(relay, targetHost, MessageKind.FileTransferStart, new FileTransferStartMessage(names, totalBytes));

        // stream chunks directly — master already negotiated with target
        long totalSent = 0;
        try
        {
            var sha = await TarGzStreamer.StreamAsync(pathList, async (data, seq, uncompressedBytes) =>
            {
                cts.Token.ThrowIfCancellationRequested();
                var chunkPayload = MessageSerializer.Encode(MessageKind.FileTransferChunk, new FileTransferChunkMessage(seq, data));
                await relay.Send([targetHost], chunkPayload);
                totalSent += data.Length;
                _dialog.UpdateProgress(uncompressedBytes, CalcSpeed(startTick, totalSent));
                _log.LogDebug("Sent chunk #{Seq}: {Bytes} bytes", seq, data.Length);
            }, _dialog.SetCurrentFile, cts.Token);

            var donePayload = MessageSerializer.Encode(MessageKind.FileTransferDone, new FileTransferDoneMessage(totalSent, sha));
            await relay.Send([targetHost], donePayload);
            _dialog.ShowCompleted();
            _log.LogInformation("Transfer complete: {Bytes} compressed bytes sent", ByteSize.FromBytes(totalSent));
        }
        catch (OperationCanceledException) when (cts.Token.IsCancellationRequested)
        {
            _log.LogInformation("Transfer cancelled");
            _dialog.Close();
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
            }
        }
    }

    // starts an outbound transfer to targetHost (case 1: master is source).
    public void StartSend(List<string> paths, string targetHost, IRelaySender relay, string localHost = "")
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
        _ = Task.Run(() => StreamAsync(paths, names, totalBytes, tick, targetHost, localHost, relay, cts.Token));
    }

    private async Task StreamAsync(List<string> paths, string[] names, long totalBytes, long startTick, string targetHost, string localHost, IRelaySender relay, CancellationToken cancel)
    {
        long totalSent = 0;
        try
        {
            var sourceHost = string.IsNullOrEmpty(localHost) ? null : localHost;
            // send request first — receiver validates its paste dir and sends Accepted before we stream
            var requestPayload = MessageSerializer.Encode(MessageKind.FileTransferRequest, new FileTransferRequestMessage(SourceHost: sourceHost));
            await relay.Send([targetHost], requestPayload);

            // wait for receiver to validate the paste destination before streaming
            TaskCompletionSource<bool>? acceptTcs;
            lock (_lock) acceptTcs = _sendAcceptTcs;
            if (acceptTcs != null) await acceptTcs.Task.WaitAsync(TimeSpan.FromMilliseconds(_watchdogTimeoutMs), cancel);

            // inform receiver of what's coming before streaming
            var startPayload = MessageSerializer.Encode(MessageKind.FileTransferStart, new FileTransferStartMessage(names, totalBytes));
            await relay.Send([targetHost], startPayload);

            var sha = await TarGzStreamer.StreamAsync(paths, async (data, seq, uncompressedBytes) =>
            {
                cancel.ThrowIfCancellationRequested();
                var chunkPayload = MessageSerializer.Encode(MessageKind.FileTransferChunk, new FileTransferChunkMessage(seq, data));
                await relay.Send([targetHost], chunkPayload);
                totalSent += data.Length;
                var speed = CalcSpeed(startTick, totalSent);
                _dialog.UpdateProgress(uncompressedBytes, speed);
                _log.LogDebug("Sent chunk #{Seq}: {Bytes} bytes", seq, data.Length);
            }, _dialog.SetCurrentFile, cancel);

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
                _sendAcceptTcs = null;
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
        CleanupReceiver(expected, fromWatchdog: true);
        SendTo(relay, expected.SourceHost, MessageKind.FileTransferAbort, new FileTransferAbortMessage(reason));
        _dialog.ShowError($"Transfer aborted: {reason}");
    }

    // shared receiver setup; dialog is shown later from HandleFileTransferStart when names/total are known
    private void SetupReceiverInternal(string sourceHost, string destFolder, IRelaySender relay)
    {
        var newReceiver = new ReceiverTransfer(sourceHost, [], 0, _watchdogTimeoutMs);
        ReceiverTransfer? existing;
        lock (_lock) { existing = _receiver; _receiver = newReceiver; _recvRelay = relay; }
        if (existing != null) CleanupReceiver(existing);
        var tempDir = TransferTempDir();
        CleanupTempDir(tempDir);
        var cts = newReceiver.Cts;
        cts.Token.Register(() => CleanupTempDir(tempDir));

        var extractor = new TarGzExtractor(tempDir, _dialog.SetCurrentFile, cts.Token);
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
            var watchdogRelay = relay;
            recv.Watchdog = new Timer(_ =>
            {
                if (newReceiver.WatchdogExpired())
                    AbortReceive(watchdogRelay, newReceiver, "transfer timed out");
            }, null, TimeSpan.FromMilliseconds(_watchdogTimeoutMs), TimeSpan.FromMilliseconds(_watchdogTimeoutMs));
        }
    }

    private static void CleanupReceiver(ReceiverTransfer receiver, bool fromWatchdog = false)
    {
        if (receiver.Watchdog is { } watchdog)
        {
            if (fromWatchdog)
                watchdog.Dispose();
            else
            {
                using var done = new ManualResetEvent(false);
                watchdog.Dispose(done);
                done.WaitOne(WatchdogDisposalWaitMs);
            }
        }
        receiver.Dispose();
        if (receiver.TempDir != null)
            CleanupTempDir(receiver.TempDir);
    }

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

    private static string TransferTempDir() => Path.Combine(Path.GetTempPath(), "hydra", "transfer");

    public void Dispose()
    {
        _dialog.CancelRequested -= HandleCancelRequested;
        Abort(relay: null, "service stopped");
    }

    public sealed record FileCopyState(string SourceHost, string[] Paths);

    private sealed class ReceiverTransfer(string sourceHost, string[] fileNames, long totalBytes, long watchdogTimeoutMs) : IDisposable
    {
        private long _lastChunkTick = Environment.TickCount64;

        public string SourceHost { get; } = sourceHost;
        public string[] FileNames { get; set; } = fileNames;
        public long TotalBytes { get; set; } = totalBytes;
        public TarGzExtractor? Extractor { get; set; }
        public string? TempDir { get; set; }
        public string? DestFolder { get; set; }
        public long TransferStartTick { get; set; }
        public Timer? Watchdog { get; set; }
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

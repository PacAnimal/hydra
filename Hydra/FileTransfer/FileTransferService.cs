using ByteSizeLib;
using Cathedral.Extensions;
using Cathedral.Utils;
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
    // how long a transfer may go without a reply or a chunk before it is abandoned
    private static readonly TimeSpan WatchdogTimeout = TimeSpan.FromSeconds(30);
    private const int WatchdogDisposalWaitMs = 1000;  // how long to wait for in-flight watchdog callback on cleanup

    private readonly TimeProvider _clock;

    // abort reason sent when the receiver has no file manager window open — checked by master for OSD routing
    public const string ReasonNoFolder = "no folder to paste into";

    // shown for an abort that arrives without a readable reason
    public const string ReasonUnknown = "no reason given";

    private const string ReasonUserCancelled = "user cancelled";
    private const string ReasonTimedOut = "transfer timed out";

    // OSD on the paste target: once it has accepted the transfer, then once the files are in place
    public const string PastingOsd = "Pasting…";
    public const string PastedOsd = "Pasted!";

    // OSD for a copy or paste that cannot start
    public const string InProgressOsd = "Transfer in progress";
    public const string InvalidTargetOsd = "Invalid paste target";
    public const string NotSupportedOsd = "Action not supported";

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
    private ITimer? _coordWatchdog; // fires if the target never replies, so a vanished peer can't wedge FileTransferOngoing
    private int _coordGeneration;  // bumped on every clear; a stale watchdog callback checks it so it can't clear a newer coordination

    // receiver side
    private ReceiverTransfer? _receiver;

    // true while any transfer is in flight (sending, receiving, or coordinating)
    public bool FileTransferOngoing { get { lock (_lock) return OngoingLocked; } }

    // true if we are currently sending to the given host (case 1)
    public bool IsSendingTo(string host) { lock (_lock) return SendingToLocked(host); }

    // true if we are currently receiving from the given host (case 2: master is target)
    public bool IsReceivingFrom(string host) { lock (_lock) return ReceivingFromLocked(host); }

    // true if we are coordinating a slave→slave transfer that the given target host accepted/aborted (case 3)
    public bool IsCoordinatingTransferTo(string targetHost) { lock (_lock) return CoordinatingLocked(targetHost); }

    // caller must hold _lock
    private bool OngoingLocked => _sendCts != null || _receiver != null || _coordTargetHost != null;
    private bool SendingToLocked(string host) => _sendTargetHost != null && _sendTargetHost.EqualsIgnoreCase(host);
    private bool ReceivingFromLocked(string host) => _receiver != null && _receiver.SourceHost.EqualsIgnoreCase(host);
    private bool CoordinatingLocked(string host) => _coordTargetHost != null && _coordTargetHost.EqualsIgnoreCase(host);

    // stores source host + paths from the last copy hotkey press
    public void SetCopyBuffer(string sourceHost, IReadOnlyCollection<string> paths)
    {
        lock (_lock) _copyBuffer = new FileCopyState(sourceHost, [.. paths]);
        if (_log.IsEnabled(LogLevel.Information))
            _log.LogInformation("Copy buffer set: {Count} {Items} from {Host}", paths.Count, Pluralize.Of(paths.Count, "item"), sourceHost);
    }

    // the OSD text for a copy of count items
    internal static string CopiedMessage(int count) => $"{count} {Pluralize.Of(count, "item")} copied";

    // what a copy-hotkey press found, local or remote; returns the OSD text to display
    public string HandleSelectionResponse(string sourceHost, FileSelectionResponseMessage msg)
    {
        if (msg.Message != null)
            return msg.Message;
        if (msg.Paths is { Length: > 0 })
        {
            SetCopyBuffer(sourceHost, msg.Paths);
            return CopiedMessage(msg.Paths.Length);
        }
        // the latest copy wins even when empty, as a local one does
        ClearCopyBuffer();
        if (_log.IsEnabled(LogLevel.Information))
            _log.LogInformation("Copy buffer cleared: nothing selected on {Host}", sourceHost);
        return "0 items selected";
    }

    public FileCopyState? GetCopyBuffer() { lock (_lock) return _copyBuffer; }
    public void ClearCopyBuffer() { lock (_lock) _copyBuffer = null; }

    private double CalcSpeed(long startTimestamp, long bytes)
    {
        var elapsed = _clock.GetElapsedTime(startTimestamp).TotalSeconds;
        return elapsed > SpeedMinElapsedSec ? bytes / elapsed : 0;
    }

    private ReceiverTransfer? TryClearReceiver()
    {
        lock (_lock)
        {
            var receiver = _receiver;
            _receiver = null;
            return receiver;
        }
    }

    // claims the send slot for a new transfer; null while another send or a receive holds it.
    // awaitAccept arms the wait for the receiver's FileTransferAccepted. The token is read here, under the lock,
    // because a concurrent cancel may dispose the source before the caller gets to it.
    private SendClaim? TryClaimSendSlot(string targetHost, IRelaySender relay, bool awaitAccept)
    {
        lock (_lock)
        {
            if (_sendCts != null || _receiver != null) return null;
            _sendCts = new CancellationTokenSource();
            _sendTargetHost = targetHost;
            _sendRelay = relay;
            if (awaitAccept) _sendAcceptTcs = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            return new SendClaim(_sendCts, _sendCts.Token);
        }
    }

    // A cancelled send releases the slot before its async worker has necessarily reached finally. Only the
    // operation that still owns the slot may clear it; otherwise an old worker can dispose a replacement send.
    // A null expected releases whichever send holds the slot. Returns null if nothing was released.
    private ReleasedPeer? ReleaseSendSlot(CancellationTokenSource? expected, bool cancel)
    {
        CancellationTokenSource cts;
        ReleasedPeer released;
        lock (_lock)
        {
            if (_sendCts == null || (expected != null && !ReferenceEquals(_sendCts, expected))) return null;
            cts = _sendCts;
            released = new ReleasedPeer(_sendTargetHost!, _sendRelay!);
            _sendCts = null;
            _sendTargetHost = null;
            _sendRelay = null;
            _sendAcceptTcs = null;
        }
        if (cancel) cts.Cancel();
        cts.Dispose();
        return released;
    }

    // a send that lost the slot must not report progress: the dialog may already belong to the next transfer
    private bool OwnsSendSlot(SendClaim claim)
    {
        lock (_lock) return ReferenceEquals(_sendCts, claim.Owner);
    }

    // as OwnsSendSlot, for a chunk still being written when its receive ended
    private bool OwnsReceive(ReceiverTransfer receiver)
    {
        lock (_lock) return ReferenceEquals(_receiver, receiver);
    }

    // sizes and names the payload and shows the transfer; on failure the slot is released before onFailure
    // reports it, and null is returned. Only an operation that still owned the slot may report its outcome:
    // once released, the dialog may already belong to the next transfer.
    private PreparedSend? TryPrepareSend(List<string> paths, SendClaim claim, Action<Exception> onFailure)
    {
        PreparedSend prepared;
        try
        {
            var names = paths.Select(Path.GetFileName).Where(n => n != null).Cast<string>().ToArray();
            prepared = new PreparedSend(paths, names, TarGzStreamer.ComputeTotalBytes(paths), _clock.GetTimestamp());
        }
        catch (Exception ex)
        {
            if (ReleaseSendSlot(claim.Owner, cancel: true) != null) onFailure(ex);
            return null;
        }

        _dialog.ShowTransferring(new FileTransferInfo(prepared.Names, prepared.TotalBytes, IsSender: true));
        return prepared;
    }

    // Reached for our own cancellation and for a chunk send cancelled by the relay's own internal token on
    // disconnect. Both mean the same thing here: stop gracefully, not a genuine transfer failure.
    private void OnSendCancelled(SendClaim claim)
    {
        _log.LogInformation("Transfer cancelled");
        if (ReleaseSendSlot(claim.Owner, cancel: false) != null) _dialog.Close();
    }

    // clears coordinator state and returns the target it was coordinating (null if not coordinating)
    private ReleasedPeer? TryClearCoordinator()
    {
        lock (_lock)
        {
            var released = _coordTargetHost != null ? new ReleasedPeer(_coordTargetHost, _coordRelay!) : null;
            ClearCoordinatorLocked();
            return released;
        }
    }

    // clears all coordinator state and stops the watchdog
    private void ClearCoordinatorLocked()
    {
        _coordTargetHost = null;
        _coordSourceHost = null;
        _coordSourcePaths = null;
        _coordRelay = null;
        _coordWatchdog?.Dispose();
        _coordWatchdog = null;
        _coordGeneration++; // invalidate any already-queued watchdog callback
    }

    // fired by the coordinator watchdog: if the target never answered the FileTransferRequest, clear the
    // stale coordinator state so it doesn't block all future transfers (FileTransferOngoing == true forever).
    // gen guards against a callback that was already queued when the coordination it belonged to was cleared.
    private void CoordinatorTimedOut(int gen, string targetHost)
    {
        bool cleared = false;
        lock (_lock)
        {
            if (_coordGeneration == gen && _coordTargetHost != null)
            {
                ClearCoordinatorLocked();
                cleared = true;
            }
        }
        if (cleared) _log.LogWarning("Coordinated transfer to {Target} timed out with no response — cleared", targetHost);
    }

    // aborts an in-flight transfer if its peer (send target / receiver source / coordinator target) is no
    // longer among the current peers. Without this, a sender whose target vanishes keeps streaming into the
    // void and then falsely reports "complete"; a receiver/coordinator would hang until its own watchdog.
    public void AbortIfPeerGone(IReadOnlyCollection<string> currentPeers, IRelaySender relay)
    {
        bool Gone(string? host) => host != null && !currentPeers.Contains(host, StringComparer.OrdinalIgnoreCase);
        string? gone = null;
        lock (_lock)
        {
            if (Gone(_sendTargetHost)) gone = _sendTargetHost;
            else if (Gone(_receiver?.SourceHost)) gone = _receiver!.SourceHost;
            else if (Gone(_coordTargetHost)) gone = _coordTargetHost;
        }
        if (gone != null)
        {
            if (_log.IsEnabled(LogLevel.Information))
                _log.LogInformation("Aborting transfer — peer '{Host}' left", gone);
            Abort(relay, $"peer '{gone}' left");
        }
    }

    public static bool IsFileTransferMessage(MessageKind kind) => kind is
        MessageKind.FileTransferRequest or MessageKind.FileTransferStart or
        MessageKind.FileTransferChunk or MessageKind.FileTransferDone or
        MessageKind.FileTransferAbort or MessageKind.FileTransferAccepted;

    // called when a slave responds FileTransferBusy to a request we sent; cleans up local state
    public void HandleBusy(string busyHost)
    {
        bool matchesSend, matchesReceive, matchesCoord;
        lock (_lock)
        {
            matchesSend = SendingToLocked(busyHost);
            matchesReceive = ReceivingFromLocked(busyHost);
            matchesCoord = CoordinatingLocked(busyHost);
        }

        if (matchesSend && ReleaseSendSlot(null, cancel: true) != null)
        {
            _dialog.Close();
            if (_log.IsEnabled(LogLevel.Information))
                _log.LogInformation("Transfer to {Host} refused: host is busy", busyHost);
            return;
        }

        if (matchesCoord)
        {
            TryClearCoordinator();
            if (_log.IsEnabled(LogLevel.Information))
                _log.LogInformation("Coordination refused by target {Host}: busy", busyHost);
            return;
        }

        if (matchesReceive)
        {
            if (TryClearReceiver() is { } receiver) CleanupReceiver(receiver);
            _dialog.Close();
            if (_log.IsEnabled(LogLevel.Information))
                _log.LogInformation("Source {Host} refused to stream: busy", busyHost);
            return;
        }

        // case 3 after accept: coordinator state already cleared; target will watchdog
        if (_log.IsEnabled(LogLevel.Information))
            _log.LogInformation("Host {Host} is busy (no matching transfer state)", busyHost);
    }

    internal static FileTransferService Null() =>
        new(new NullFileTransferDialog(), new NullDropTargetResolver(), Microsoft.Extensions.Logging.Abstractions.NullLogger<FileTransferService>.Instance);

    public FileTransferService(IFileTransferDialog dialog, IDropTargetResolver dropTargetResolver, ILogger<FileTransferService> log, TimeProvider? clock = null)
    {
        _dialog = dialog;
        _dropTargetResolver = dropTargetResolver;
        _log = log;
        _clock = clock ?? TimeProvider.System;
        // subscribe once — reads current state on each cancel click
        dialog.CancelRequested += HandleCancelRequested;
    }

    // called when relay disconnects or peer departs during an active transfer
    public void Abort(IRelaySender? relay, string reason)
    {
        var receiver = TryClearReceiver();
        foreach (var peer in new[] { ReleaseSendSlot(null, cancel: true), TryClearCoordinator(), receiver?.Peer })
            if (peer != null) SendAbort(relay ?? peer.Relay, peer.Host, reason);
        if (receiver != null) CleanupReceiver(receiver);
        _dialog.Close();
    }

    // true when this message completed a receive, its files in place
    public async Task<bool> OnMessage(string sourceHost, MessageKind kind, ReadOnlyMemory<byte> body, IRelaySender relay)
    {
        switch (kind)
        {
            case MessageKind.FileTransferRequest: HandleFileTransferRequest(sourceHost, body, relay); break;
            case MessageKind.FileTransferStart: HandleFileTransferStart(sourceHost, body); break;
            case MessageKind.FileTransferChunk: await HandleFileTransferChunk(sourceHost, body); break;
            case MessageKind.FileTransferDone: return await HandleFileTransferDone(sourceHost, body, relay);
            case MessageKind.FileTransferAbort: HandleFileTransferAbort(sourceHost, body); break;
            case MessageKind.FileTransferAccepted:
                {
                    // case 1: master was sending — unblock RunSend to start chunk flow
                    TaskCompletionSource<bool>? tcs;
                    // case 3: master was coordinating — tell source slave to start streaming
                    string? coordSource; string[]? coordPaths; string? coordTarget;
                    lock (_lock)
                    {
                        tcs = _sendAcceptTcs; _sendAcceptTcs = null;
                        coordSource = _coordSourceHost;
                        coordPaths = _coordSourcePaths;
                        coordTarget = _coordTargetHost;
                        ClearCoordinatorLocked(); // handed off to the source slave; stop the watchdog
                    }
                    tcs?.TrySetResult(true);
                    if (coordSource != null && coordPaths != null && coordTarget != null)
                    {
                        var req = new FileStreamRequestMessage(coordPaths, coordTarget);
                        SendTo(relay, coordSource, MessageKind.FileStreamRequest, req);
                        if (_log.IsEnabled(LogLevel.Information))
                            _log.LogInformation("Target {Target} accepted — sending FileStreamRequest to {Source}", coordTarget, coordSource);
                    }
                    break;
                }
        }
        return false;
    }

    private void HandleFileTransferRequest(string sourceHost, ReadOnlyMemory<byte> body, IRelaySender relay)
    {
        var msg = body.ParseMessage<FileTransferRequestMessage>(_log, sourceHost);
        if (msg == null) return;

        if (FileTransferOngoing)
        {
            SendTo(relay, sourceHost, MessageKind.FileTransferBusy, new FileTransferBusyMessage());
            if (_log.IsEnabled(LogLevel.Information))
                _log.LogInformation("Transfer request from {Host} refused: transfer already in progress", sourceHost);
            return;
        }

        var destFolder = _dropTargetResolver.GetPasteDirectory();
        if (string.IsNullOrEmpty(destFolder))
        {
            SendAbort(relay, sourceHost, ReasonNoFolder);
            _log.LogWarning("Transfer from {Host}: no valid paste destination — no file manager window is active", sourceHost);
            return;
        }
        if (_log.IsEnabled(LogLevel.Information))
            _log.LogInformation("Paste destination: {Dest}", destFolder);

        var dataSourceHost = msg.SourceHost ?? sourceHost;
        SetupReceiverInternal(dataSourceHost, destFolder, relay);
        SendTo(relay, sourceHost, MessageKind.FileTransferAccepted, new FileTransferAcceptedMessage());
        if (_log.IsEnabled(LogLevel.Information))
            _log.LogInformation("Transfer request from {Host}: data expected from {DataSource}", sourceHost, dataSourceHost);
    }

    private void HandleFileTransferStart(string sourceHost, ReadOnlyMemory<byte> body)
    {
        var msg = body.ParseMessage<FileTransferStartMessage>(_log, sourceHost);
        if (msg == null) return;

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
        if (_log.IsEnabled(LogLevel.Information))
            _log.LogInformation("Transfer start from {Host}: {Count} {Files}, {Total} bytes", sourceHost, msg.FileNames.Length, Pluralize.Of(msg.FileNames.Length, "file"), msg.TotalBytes);
    }

    private async Task HandleFileTransferChunk(string sourceHost, ReadOnlyMemory<byte> body)
    {
        var chunk = body.ParseMessage<FileTransferChunkMessage>(_log, sourceHost);
        if (chunk == null) return;
        var (sequence, data) = (chunk.Sequence, chunk.Data);
        ReceiverTransfer? receiver;
        lock (_lock) receiver = _receiver;
        if (receiver?.Extractor == null) return;
        if (!receiver.SourceHost.EqualsIgnoreCase(sourceHost))
        {
            _log.LogError("FileTransferChunk from unexpected host {Host} (expected {Expected}) — dropping", sourceHost, receiver.SourceHost);
            return;
        }

        try { await receiver.Extractor.WriteChunk(data); }
        catch (Exception e) when (e is InvalidOperationException or ObjectDisposedException) { return; } // pipe completed (cleanup raced with this chunk)
        receiver.TouchWatchdog();
        var extracted = receiver.Extractor.BytesExtracted;
        var speed = CalcSpeed(receiver.TransferStart, receiver.Extractor.BytesReceived);
        if (OwnsReceive(receiver)) _dialog.UpdateProgress(extracted, speed);
        if (_log.IsEnabled(LogLevel.Debug))
            _log.LogDebug("Chunk #{Seq} from {Host}: {Bytes} bytes", sequence, sourceHost, data.Length);
    }

    private async Task<bool> HandleFileTransferDone(string sourceHost, ReadOnlyMemory<byte> body, IRelaySender relay)
    {
        var msg = body.ParseMessage<FileTransferDoneMessage>(_log, sourceHost);

        ReceiverTransfer? receiver;
        lock (_lock)
        {
            receiver = _receiver;
            if (ReceivingFromLocked(sourceHost)) _receiver = null;
            else receiver = null;
        }
        if (receiver == null) return false;

        if (msg == null || receiver.Extractor == null)
        {
            FailReceive(receiver, relay, "unreadable completion from the sender");
            return false;
        }

        return await FinalizeReceiving(receiver, msg, relay);
    }

    private void HandleFileTransferAbort(string sourceHost, ReadOnlyMemory<byte> body)
    {
        var reason = body.ParseMessage<FileTransferAbortMessage>(_log, sourceHost)?.Reason;
        if (string.IsNullOrEmpty(reason)) reason = ReasonUnknown;

        bool relevant;
        lock (_lock)
        {
            var coordinating = CoordinatingLocked(sourceHost);
            relevant = SendingToLocked(sourceHost) || ReceivingFromLocked(sourceHost) || coordinating;

            // clear coordinator state if target aborted
            if (coordinating) ClearCoordinatorLocked();
        }

        if (!relevant)
        {
            if (_log.IsEnabled(LogLevel.Debug))
                _log.LogDebug("FileTransferAbort from unexpected host {Host} — ignoring", sourceHost);
            return;
        }

        if (ReleaseSendSlot(null, cancel: true) != null)
        {
            if (_log.IsEnabled(LogLevel.Information))
                _log.LogInformation("Transfer send cancelled by {Host}: {Reason}", sourceHost, reason);
            _dialog.Close();
            return;
        }
        if (TryClearReceiver() is not { } receiver) return;
        CleanupReceiver(receiver);
        _dialog.ShowError($"Transfer aborted: {reason}");
        _log.LogWarning("Transfer aborted by {Host}: {Reason}", sourceHost, reason);
    }

    // -- private helpers --

    private void HandleCancelRequested()
    {
        if (ReleaseSendSlot(null, cancel: true) is { } send)
        {
            SendAbort(send.Relay, send.Host, ReasonUserCancelled);
            _dialog.Close();
            _log.LogInformation("Transfer send cancelled by user");
            return;
        }

        if (TryClearReceiver() is not { } receiver) return;

        CleanupReceiver(receiver);
        _dialog.Close();
        SendAbort(receiver.Relay, receiver.SourceHost, ReasonUserCancelled);
        _log.LogInformation("Transfer receive cancelled by user");
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
            if (_log.IsEnabled(LogLevel.Information))
                _log.LogInformation("Paste: master as receiver from {Source} — told source to stream", sourceHost);
            return true;
        }

        if (sourceHost.EqualsIgnoreCase(localHost))
        {
            // case 1: master is source — send FileTransferRequest to target, await accepted, then stream
            InitiateSend([.. paths], targetHost, relay, localHost);
            return true;
        }

        // case 3: slave→slave — tell target to expect data from sourceHost; on accept, tell source to stream
        lock (_lock)
        {
            if (OngoingLocked)
            {
                _log.LogWarning("Paste: transfer already in progress");
                return true;
            }
            _coordTargetHost = targetHost;
            _coordSourceHost = sourceHost;
            _coordSourcePaths = paths;
            _coordRelay = relay;
            // watchdog: if the target never replies (Accepted/Busy/Abort), clear the coordinator state so a
            // vanished peer can't leave FileTransferOngoing stuck true and block all future transfers.
            var gen = _coordGeneration;
            _coordWatchdog = _clock.CreateTimer(_ => CoordinatorTimedOut(gen, targetHost), null, WatchdogTimeout, Timeout.InfiniteTimeSpan);
        }
        SendTo(relay, targetHost, MessageKind.FileTransferRequest, new FileTransferRequestMessage(SourceHost: sourceHost));
        if (_log.IsEnabled(LogLevel.Information))
            _log.LogInformation("Paste: sent FileTransferRequest to {Target} (data from {Source})", targetHost, sourceHost);
        return true;
    }

    // streams files to targetHost on the slave side in response to a FileStreamRequest.
    // master already negotiated with the target — go straight to FileTransferStart + chunks.
    public async Task ExecuteStreamRequest(string[] paths, string targetHost, IRelaySender relay)
    {
        var claim = TryClaimSendSlot(targetHost, relay, awaitAccept: false);
        if (claim == null) return;

        var prepared = TryPrepareSend([.. paths], claim, ex =>
        {
            SendAbort(relay, targetHost, ex.Message);
            _dialog.ShowError($"Transfer failed: {ex.Message}");
            _log.LogWarning(ex, "ExecuteStreamRequest pre-stream setup failed");
        });
        if (prepared == null) return;

        try
        {
            await RunSendCore(prepared, targetHost, relay, claim);
        }
        finally
        {
            ReleaseSendSlot(claim.Owner, cancel: false);
        }
    }

    // starts an outbound transfer from the master (case 1: master is source).
    // sends FileTransferRequest, waits for acceptance, then streams chunks.
    public void InitiateSend(List<string> paths, string targetHost, IRelaySender relay, string localHost = "")
    {
        var claim = TryClaimSendSlot(targetHost, relay, awaitAccept: true);
        if (claim == null) return;

        var prepared = TryPrepareSend(paths, claim, ex =>
        {
            _log.LogWarning(ex, "InitiateSend pre-stream setup failed");
            _dialog.ShowError($"Transfer failed: {ex.Message}");
        });
        if (prepared == null) return;

        _ = Task.Run(() => RunSend(prepared, targetHost, localHost, relay, claim));
    }

    private async Task RunSend(PreparedSend prepared, string targetHost, string localHost, IRelaySender relay, SendClaim claim)
    {
        var cancel = claim.Token;
        try
        {
            var sourceHost = string.IsNullOrEmpty(localHost) ? null : localHost;
            // send request first — receiver validates its paste dir and sends Accepted before we stream
            relay.Send(targetHost, MessageKind.FileTransferRequest, new FileTransferRequestMessage(SourceHost: sourceHost));

            // wait for receiver to accept before streaming
            TaskCompletionSource<bool>? acceptTcs;
            lock (_lock) acceptTcs = _sendAcceptTcs;
            if (acceptTcs != null) await acceptTcs.Task.WaitAsync(WatchdogTimeout, _clock, cancel);

            await RunSendCore(prepared, targetHost, relay, claim);
        }
        catch (OperationCanceledException)
        {
            OnSendCancelled(claim);
        }
        catch (TimeoutException)
        {
            _log.LogWarning("Transfer timed out waiting for {Target} to respond", targetHost);
            if (ReleaseSendSlot(claim.Owner, cancel: false) != null)
            {
                SendAbort(relay, targetHost, ReasonTimedOut);
                _dialog.ShowError("Transfer failed: destination did not respond");
            }
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "Unexpected error during file transfer to {Target}", targetHost);
            if (ReleaseSendSlot(claim.Owner, cancel: false) != null) _dialog.ShowError($"Transfer failed: {ex.Message}");
        }
        finally
        {
            ReleaseSendSlot(claim.Owner, cancel: false);
        }
    }

    // shared chunk-streaming core: FileTransferStart → chunks → FileTransferDone.
    // handles cancellation and generic errors internally, releasing the slot before reporting any outcome so
    // whoever sees it may start the next transfer, and reporting nothing if the slot was no longer ours;
    // callers' own finally cleanup covers everything else.
    private async Task RunSendCore(PreparedSend prepared, string targetHost, IRelaySender relay, SendClaim claim)
    {
        var (paths, names, totalBytes, startTimestamp) = prepared;
        var cancel = claim.Token;
        long totalSent = 0;
        try
        {
            // a send cancelled before it began must not open a stream its abort has already closed
            cancel.ThrowIfCancellationRequested();
            relay.Send(targetHost, MessageKind.FileTransferStart, new FileTransferStartMessage(names, totalBytes));

            var sha = await TarGzStreamer.Compress(paths, async (data, seq, uncompressedBytes) =>
            {
                cancel.ThrowIfCancellationRequested();
                await relay.SendReliableAsync(
                    [targetHost],
                    MessageSerializer.Encode(MessageKind.FileTransferChunk, new FileTransferChunkMessage(seq, data)),
                    cancel);
                totalSent += data.Length;
                if (OwnsSendSlot(claim)) _dialog.UpdateProgress(uncompressedBytes, CalcSpeed(startTimestamp, totalSent));
                if (_log.IsEnabled(LogLevel.Debug))
                    _log.LogDebug("Sent chunk #{Seq}: {Bytes} bytes", seq, data.Length);
            }, name =>
            {
                if (OwnsSendSlot(claim)) _dialog.SetCurrentFile(name);
            }, cancel);

            relay.Send(targetHost, MessageKind.FileTransferDone, new FileTransferDoneMessage(totalSent, sha));
            if (_log.IsEnabled(LogLevel.Information))
                _log.LogInformation("Transfer complete: {Bytes} compressed bytes sent", ByteSize.FromBytes(totalSent));
            if (ReleaseSendSlot(claim.Owner, cancel: false) != null) _dialog.ShowCompleted();
        }
        catch (OperationCanceledException)
        {
            OnSendCancelled(claim);
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "Transfer failed");
            if (ReleaseSendSlot(claim.Owner, cancel: false) != null)
            {
                SendAbort(relay, targetHost, ex.Message);
                _dialog.ShowError($"Transfer failed: {ex.Message}");
            }
        }
    }

    private async Task<bool> FinalizeReceiving(ReceiverTransfer receiver, FileTransferDoneMessage msg, IRelaySender relay)
    {
        try
        {
            await receiver.Extractor!.Complete();
            var actualHash = receiver.Extractor.GetHash();
            var bytesReceived = receiver.Extractor.BytesReceived;
            var hashMatch = actualHash.SequenceEqual(msg.Sha256);

            if (bytesReceived != msg.TotalBytesSent || !hashMatch)
            {
                CleanupReceiver(receiver);
                _dialog.ShowError("Transfer failed: data integrity check failed");
                _log.LogWarning("Integrity failure from {Host} (received={Received}, expected={Expected}, hashMatch={HashMatch})",
                    receiver.SourceHost, bytesReceived, msg.TotalBytesSent, hashMatch);
                return false;
            }

            _dropTargetResolver.MoveToDestination(receiver.TempDir!, receiver.DestFolder!);
            CleanupReceiver(receiver);
            _dialog.ShowCompleted();
            if (_log.IsEnabled(LogLevel.Information))
                _log.LogInformation("Transfer complete: files in {Dest}", receiver.DestFolder);
            return true;
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "Failed to finalize transfer from {Host}", receiver.SourceHost);
            FailReceive(receiver, relay, ex.Message);
            return false;
        }
    }

    // ends a receive that is already off the books: drops its files and tells the sender and the user
    private void FailReceive(ReceiverTransfer receiver, IRelaySender relay, string reason)
    {
        CleanupReceiver(receiver);
        SendAbort(relay, receiver.SourceHost, reason);
        _dialog.ShowError($"Transfer failed: {reason}");
    }

    private void AbortReceive(ReceiverTransfer expected, string reason)
    {
        lock (_lock)
        {
            if (_receiver != expected) return;
            _receiver = null;
        }
        CleanupReceiver(expected, fromWatchdog: true);
        SendAbort(expected.Relay, expected.SourceHost, reason);
        _dialog.ShowError($"Transfer aborted: {reason}");
    }

    // shared receiver setup; dialog is shown later from HandleFileTransferStart when names/total are known
    private void SetupReceiverInternal(string sourceHost, string destFolder, IRelaySender relay)
    {
        var newReceiver = new ReceiverTransfer(sourceHost, relay, _clock);
        ReceiverTransfer? existing;
        lock (_lock) { existing = _receiver; _receiver = newReceiver; }
        if (existing != null) CleanupReceiver(existing);
        var tempDir = TransferTempDir();
        CleanupTempDir(tempDir);
        var cts = newReceiver.Cts;
        cts.Token.Register(() => CleanupTempDir(tempDir));

        var extractor = new TarGzExtractor(tempDir, name => { if (OwnsReceive(newReceiver)) _dialog.SetCurrentFile(name); }, cts.Token);
        lock (_lock)
        {
            if (_receiver != newReceiver) { extractor.Dispose(); return; }
            var recv = _receiver!;
            recv.Extractor = extractor;
            recv.TempDir = tempDir;
            recv.DestFolder = destFolder;
            recv.TransferStart = _clock.GetTimestamp();
            recv.TouchWatchdog();
            recv.Watchdog = _clock.CreateTimer(_ =>
            {
                if (newReceiver.WatchdogExpired())
                    AbortReceive(newReceiver, ReasonTimedOut);
            }, null, WatchdogTimeout, WatchdogTimeout);
        }
    }

    private static void CleanupReceiver(ReceiverTransfer receiver, bool fromWatchdog = false)
    {
        if (receiver.Watchdog is { } watchdog)
        {
            // from anywhere but its own callback, waits for a callback in flight
            if (fromWatchdog)
                watchdog.Dispose();
            else
                watchdog.DisposeAsync().AsTask().Wait(WatchdogDisposalWaitMs);
        }
        receiver.Dispose();
        if (receiver.TempDir != null)
            CleanupTempDir(receiver.TempDir);
    }

    private void SendAbort(IRelaySender relay, string host, string reason) =>
        SendTo(relay, host, MessageKind.FileTransferAbort, new FileTransferAbortMessage(reason));

    private void SendTo<T>(IRelaySender relay, string host, MessageKind kind, T msg)
    {
        try { relay.Send(host, kind, msg); }
        catch (Exception ex) { _log.LogWarning(ex, "SendTo failed"); }
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

    // the peer a released transfer was with, and the relay that reaches it
    private sealed record ReleasedPeer(string Host, IRelaySender Relay);

    private sealed record SendClaim(CancellationTokenSource Owner, CancellationToken Token);

    private sealed record PreparedSend(List<string> Paths, string[] Names, long TotalBytes, long StartTimestamp);

    private sealed class ReceiverTransfer(string sourceHost, IRelaySender relay, TimeProvider clock) : IDisposable
    {
        private long _lastChunk = clock.GetTimestamp();

        public string SourceHost { get; } = sourceHost;
        public IRelaySender Relay { get; } = relay;
        public ReleasedPeer Peer => new(SourceHost, Relay);
        public string[] FileNames { get; set; } = [];
        public long TotalBytes { get; set; }
        public TarGzExtractor? Extractor { get; set; }
        public string? TempDir { get; set; }
        public string? DestFolder { get; set; }
        public long TransferStart { get; set; }
        public ITimer? Watchdog { get; set; }
        public CancellationTokenSource Cts { get; } = new();

        public void TouchWatchdog() => Interlocked.Exchange(ref _lastChunk, clock.GetTimestamp());
        public bool WatchdogExpired() => clock.GetElapsedTime(Interlocked.Read(ref _lastChunk)) >= WatchdogTimeout;
        public FileTransferInfo ToTransferInfo() => new(FileNames, TotalBytes, IsSender: false);

        public void Dispose()
        {
            Cts.Cancel();
            Extractor?.Dispose();
            Cts.Dispose();
        }
    }
}

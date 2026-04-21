using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading.Channels;
using Cathedral.Extensions;
using Hydra.Config;
using Hydra.FileTransfer;
using Hydra.Keyboard;
using Hydra.Mouse;
using Hydra.Platform;
using Hydra.Relay;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Hydra.Screen;

public class InputRouter(
    IPlatformInput platform,
    IHydraProfile profile,
    IRelaySender relay,
    IScreenDetector screens,
    ILoggerFactory loggerFactory,
    ILogger<InputRouter> log,
    IScreenSaverSync screenSaverSync,
    IClipboardSync clipboardSync,
    FileTransferService fileTransfer,
    IFileSelectionDetector selectionDetector,
    IOsdNotification osd,
    IWorldState? peerState = null)
    : IHostedService
{
    private const KeyModifiers LockHotkey = KeyModifiers.Control | KeyModifiers.Alt | KeyModifiers.Super;

    private const int MaxMouseHz = 125; // should divide evenly by 1000
    private const int MinMouseIntervalMs = 1000 / MaxMouseHz;

    // cached key repeat settings from OS; refreshed periodically in the poll loop.
    // volatile: written on poll timer thread, read on event tap thread.
    private volatile int _repeatDelayMs = 500;
    private volatile int _repeatRateMs = 33;

    private readonly IWorldState _peerState = peerState ?? new WorldState();

    // channel-based actor model: single consumer processes all state mutations sequentially.
    // event tap callbacks post commands via TryWrite (non-blocking); async callers use TCS.
    private readonly LocalMasterState _state = new();
    private readonly Channel<Func<LocalMasterState, ValueTask>> _commands =
        Channel.CreateUnbounded<Func<LocalMasterState, ValueTask>>(
            new UnboundedChannelOptions { SingleReader = true, AllowSynchronousContinuations = false });
    private Task? _consumerTask;

    private CancellationTokenSource? _pollCts;
    private readonly IScreenSaverSync _screenSaverSync = screenSaverSync;
    private readonly IClipboardSync _clipboardSync = clipboardSync;
    private readonly FileTransferService _fileTransfer = fileTransfer;
    private readonly IFileSelectionDetector _selectionDetector = selectionDetector;
    private ClipboardSnapshot? _lastReceived;

    private static readonly long MaxClipboardBytes = ClipboardUtils.MaxClipboardBytes;

    public async Task StartAsync(CancellationToken cancellationToken)
    {
        if (!platform.IsAccessibilityTrusted())
        {
            log.LogError("Input hook permission not granted. On macOS: grant access in System Settings > Privacy & Security > Accessibility. Then restart Hydra.");
            return;
        }

        log.LogInformation("Host: {Name}", profile.Name);

        if (!profile.RemoteOnly && profile.LocalHost == null && profile.Hosts.Count > 0)
        {
            log.LogError("Host '{Name}' is not listed in the config hosts — add it to the hosts list.", profile.Name);
            return;
        }

        var snapshot = await screens.Get(cancellationToken);

        // direct state init — safe because consumer has not started yet
        var st = _state;
        st.LocalScreens = snapshot.Screens;
        st.LocalScreenEntries = snapshot.Entries;
        st.ActiveLocalScreen = st.LocalScreens.FirstOrDefault();

        if (!profile.RemoteOnly && st.ActiveLocalScreen == null)
        {
            log.LogError("No local screens detected.");
            return;
        }

        if (st.ActiveLocalScreen != null)
            UpdateWarpPoint(st, st.ActiveLocalScreen);
        if (profile.RemoteOnly)
            st.LockedToScreen = true;  // default: locked to remote; hotkey unlocks to local
        st.Screens = BuildAllScreens(st.LocalScreens);
        st.Layout = new ScreenLayout(st.Screens, profile.Hosts, profile.DeadCorners, BuildScaleMap(st.LocalScreenEntries, []), log);

        foreach (var remote in st.Screens.Where(r => !r.IsLocal))
            log.LogInformation("Remote screen '{Name}': waiting for peer", remote.Name);

        var (delayMs, rateMs) = platform.GetKeyRepeatSettings();
        _repeatDelayMs = delayMs;
        _repeatRateMs = rateMs;

        relay.PeersChanged += OnPeersChanged;
        relay.MessageReceived += OnMessageReceived;
        relay.Disconnected += OnRelayDisconnected;
        screens.ScreensChanged += OnScreensChanged;

        _pollCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);

        // start consumer before event tap so early events are processed
        _consumerTask = Task.Run(ProcessCommands, cancellationToken);

        await platform.StartEventTap((x, y) => OnMouseMove(x, y), OnMouseDelta, OnKeyEvent, OnMouseButton, OnMouseScroll);

        _screenSaverSync.StartWatching(OnScreensaverActivated, OnScreensaverDeactivated);

        _ = RefreshKeyRepeatAsync(_pollCts.Token);
    }

    public async Task StopAsync(CancellationToken cancellationToken)
    {
        _pollCts?.Cancel();
        _pollCts?.Dispose();
        relay.PeersChanged -= OnPeersChanged;
        relay.MessageReceived -= OnMessageReceived;
        relay.Disconnected -= OnRelayDisconnected;
        screens.ScreensChanged -= OnScreensChanged;

        _screenSaverSync.StopWatching();
        platform.StopEventTap();

        // drain remaining commands, then stop consumer
        _commands.Writer.TryComplete();
        if (_consumerTask != null)
            await _consumerTask;

        // consumer is done; safe to access _state directly
        if (_state.Mouse.IsOnVirtualScreen)
        {
            platform.IsOnVirtualScreen = false;
            await platform.ShowCursor();
        }
    }

    private async Task ProcessCommands()
    {
        try
        {
            await foreach (var cmd in _commands.Reader.ReadAllAsync())
                await cmd(_state);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            log.LogError(ex, "InputRouter consumer error");
        }
    }

    // posts a fence command and awaits it — all previously queued commands will have been processed on return.
    // used by tests to synchronize after firing platform events.
    internal Task FlushAsync()
    {
        if (_consumerTask == null) return Task.CompletedTask;
        var tcs = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        if (!_commands.Writer.TryWrite(_ => { tcs.TrySetResult(); return ValueTask.CompletedTask; }))
            return Task.CompletedTask;
        return tcs.Task;
    }

    private async Task OnScreensChanged(LocalScreenSnapshot snapshot)
    {
        log.LogInformation("Screen configuration changed — rebuilding layout");
        LogDetectedScreens(snapshot.Screens);
        var newScreens = BuildAllScreens(snapshot.Screens);
        var peerScreens = await _peerState.GetPeerScreensSnapshot();

        var tcs = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        if (!_commands.Writer.TryWrite(st =>
        {
            ApplyPeerScreenSizes(peerScreens, newScreens);
            st.LocalScreens = snapshot.Screens;
            st.LocalScreenEntries = snapshot.Entries;
            st.Screens = newScreens;
            st.Layout = new ScreenLayout(newScreens, profile.Hosts, profile.DeadCorners, BuildScaleMap(st.LocalScreenEntries, peerScreens), log);

            if (!st.Mouse.IsOnVirtualScreen)
            {
                st.ActiveLocalScreen = st.LocalScreens.FirstOrDefault() ?? st.ActiveLocalScreen;
                if (st.ActiveLocalScreen != null) UpdateWarpPoint(st, st.ActiveLocalScreen);
            }
            tcs.TrySetResult();
            return ValueTask.CompletedTask;
        }))
        {
            return;
        }

        await tcs.Task;
    }

    private async Task RefreshKeyRepeatAsync(CancellationToken ct)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(30));
        try
        {
            while (await timer.WaitForNextTickAsync(ct))
            {
                var (delayMs, rateMs) = platform.GetKeyRepeatSettings();
                _repeatDelayMs = delayMs;
                _repeatRateMs = rateMs;
            }
        }
        catch (OperationCanceledException) { }
    }

    private async Task OnPeersChanged(string[] hostNames)
    {
        var current = new HashSet<string>(hostNames, StringComparer.OrdinalIgnoreCase);
        var configuredSlaves = profile.RemoteHosts
            .Select(s => s.Name)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        var delta = await _peerState.UpdatePeers(current, configuredSlaves);

        var tcs = new TaskCompletionSource<(string?, int, int)>(TaskCreationOptions.RunContinuationsAsynchronously);
        if (!_commands.Writer.TryWrite(st =>
        {
            string? host = null;
            int wx = 0, wy = 0;

            if (st.Mouse.CurrentScreen != null && !current.Contains(st.Mouse.CurrentScreen.Host))
                host = LeaveVirtualScreen(st, out wx, out wy);

            if (delta.AnyDeparted) RebuildLayout(st, delta.PeerScreensSnapshot);
            tcs.TrySetResult((host, wx, wy));
            return ValueTask.CompletedTask;
        }))
        {
            return;
        }

        var (disconnectedHost, warpX, warpY) = await tcs.Task;

        if (disconnectedHost != null)
        {
            _fileTransfer.Abort(relay, $"peer '{disconnectedHost}' disconnected");
            ReturnToLocalScreen(warpX, warpY);
            await platform.ShowCursor();
            log.LogInformation("Remote peer '{Name}' disconnected — returned to local screen", disconnectedHost);
        }

        // send MasterConfig only to newly appeared peers that are configured as slaves
        foreach (var host in delta.NewPeers)
        {
            var payload = MessageSerializer.Encode(MessageKind.MasterConfig, new MasterConfigMessage(profile.LogLevel));
            _ = relay.Send([host], payload).AsTask();
            log.LogDebug("Sent MasterConfig to {Host}", host);
        }

        if (profile.RemoteOnly)
        {
            var tcs2 = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            _commands.Writer.TryWrite(async st =>
            {
                await TryEnterRemoteOnly(st);
                tcs2.TrySetResult();
            });
            await tcs2.Task;
        }
    }

    private async Task OnRelayDisconnected()
    {
        var tcs = new TaskCompletionSource<(string?, int, int)>(TaskCreationOptions.RunContinuationsAsynchronously);
        if (!_commands.Writer.TryWrite(st =>
        {
            var host = LeaveVirtualScreen(st, out var wx, out var wy);
            tcs.TrySetResult((host, wx, wy));
            return ValueTask.CompletedTask;
        }))
        {
            return;
        }

        var (disconnectedHost, warpX, warpY) = await tcs.Task;

        // reset known peers so all slaves get a fresh MasterConfig on reconnect
        await _peerState.ClearPeers();

        if (disconnectedHost != null)
        {
            _fileTransfer.Abort(relay, "relay disconnected");
            ReturnToLocalScreen(warpX, warpY);
            await platform.ShowCursor();
            log.LogWarning("Relay disconnected — returned to local screen from '{Host}'", disconnectedHost);
        }
    }

    private void OnScreensaverActivated()
    {
        var tcs = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        if (!_commands.Writer.TryWrite(async st =>
        {
            try
            {
                if (st.ScreensaverActive) return;
                st.ScreensaverActive = true;

                if (st.Mouse.IsOnVirtualScreen && st.Mouse.CurrentScreen != null)
                {
                    st.SavedScreenName = st.Mouse.CurrentScreen.Name;
                    st.SavedCursorX = (int)st.Mouse.X;
                    st.SavedCursorY = (int)st.Mouse.Y;
                    FlushMouseDelta(st);
                    var disconnectedHost = LeaveVirtualScreen(st, out var warpX, out var warpY);
                    if (disconnectedHost != null)
                    {
                        _fileTransfer.Abort(relay, "screensaver activated");
                        _ = relay.Send([disconnectedHost], MessageSerializer.Encode(MessageKind.LeaveScreen, new LeaveScreenMessage())).AsTask();
                        ReturnToLocalScreen(warpX, warpY);
                        await platform.ShowCursor();
                    }
                }
            }
            finally
            {
                tcs.TrySetResult();
            }

            await BroadcastScreensaverSync(true);
            log.LogInformation("Screensaver activated — synced to slaves");
        }))
        {
            return;
        }

        tcs.Task.GetAwaiter().GetResult();
    }

    private void OnScreensaverDeactivated()
    {
        var tcs = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        if (!_commands.Writer.TryWrite(async st =>
        {
            try
            {
                if (!st.ScreensaverActive) return;
                st.ScreensaverActive = false;
                var savedScreen = st.SavedScreenName;
                var savedX = st.SavedCursorX;
                var savedY = st.SavedCursorY;
                st.SavedScreenName = null;

                await BroadcastScreensaverSync(false);
                log.LogInformation("Screensaver deactivated — synced to slaves");

                // best-effort cursor restore: re-enter saved remote screen if still connected and accessible
                if (savedScreen != null && relay.IsConnected)
                {
                    var dest = st.Screens.FirstOrDefault(sc => !sc.IsLocal && sc.Name.EqualsIgnoreCase(savedScreen));
                    if (dest != null && dest.Width > 0)
                    {
                        var peerScreens = await _peerState.GetPeerScreensSnapshot();
                        var remoteInfo = GetRemoteScreensAndScales(st.Screens, peerScreens, dest);
                        var scale = remoteInfo.ScaleMap.GetValueOrDefault(dest.Name, 1.0m);
                        await platform.HideCursor();
                        platform.IsOnVirtualScreen = true;
                        st.Mouse.EnterScreen(dest, remoteInfo.Screens, savedX, savedY, scale, remoteInfo.ScaleMap, remoteInfo.RelativeScaleMap);
                        st.PendingDx = 0;
                        st.PendingDy = 0;
                        st.LastWarpX = st.WarpX;
                        st.LastWarpY = st.WarpY;
                        st.PendingWarpCount = 0;
                        var enterPayload = MessageSerializer.Encode(MessageKind.EnterScreen,
                            new EnterScreenMessage(dest.Name, savedX, savedY, dest.Width, dest.Height));
                        _ = relay.Send([dest.Host], enterPayload).AsTask();
                        PushClipboardToHost(dest.Host);
                        log.LogInformation("Restored cursor to '{Screen}' after screensaver", savedScreen);
                    }
                }
            }
            finally
            {
                tcs.TrySetResult();
            }
        }))
        {
            return;
        }

        tcs.Task.GetAwaiter().GetResult();
    }

    private async ValueTask BroadcastScreensaverSync(bool active)
    {
        var peerScreens = await _peerState.GetPeerScreensSnapshot();
        var hosts = peerScreens.Keys.ToArray();
        if (hosts.Length == 0) return;
        var payload = MessageSerializer.Encode(MessageKind.ScreensaverSync, new ScreensaverSyncMessage(active));
        _ = relay.Send(hosts, payload).AsTask();
    }

    private void PushClipboardToHost(string host)
    {
        try
        {
            PushClipboardToHostInner(host);
        }
        catch (Exception ex)
        {
            log.LogWarning(ex, "Failed to push clipboard to {Host}", host);
        }
    }

    private void PushClipboardToHostInner(string host)
    {
        var text = _clipboardSync.GetText() ?? _lastReceived?.Text;
        var primaryText = _clipboardSync.GetPrimaryText() ?? _lastReceived?.PrimaryText;
        var image = _clipboardSync.GetImagePng() ?? _lastReceived?.ImagePng;

        // drop fields in priority order until combined size fits
        long textBytes = text != null ? Encoding.UTF8.GetByteCount(text) : 0;
        long primaryBytes = primaryText != null ? Encoding.UTF8.GetByteCount(primaryText) : 0;
        long imageBytes = image?.Length ?? 0;
        if (textBytes + primaryBytes + imageBytes > MaxClipboardBytes)
        {
            log.LogWarning("Clipboard push too large ({Total} bytes), dropping image", textBytes + primaryBytes + imageBytes);
            image = null; imageBytes = 0;
        }
        if (textBytes + primaryBytes + imageBytes > MaxClipboardBytes)
        {
            log.LogWarning("Clipboard push still too large ({Total} bytes), dropping primary text", textBytes + primaryBytes);
            primaryText = null; primaryBytes = 0;
        }
        if (textBytes + primaryBytes + imageBytes > MaxClipboardBytes)
        {
            log.LogWarning("Clipboard push still too large ({Total} bytes), dropping text", textBytes);
            text = null;
        }

        if (!string.IsNullOrEmpty(text) || !string.IsNullOrEmpty(primaryText) || image != null)
        {
            var payload = MessageSerializer.Encode(MessageKind.ClipboardPush, new ClipboardPushMessage(text ?? "", primaryText, image));
            _ = relay.Send([host], payload).AsTask();
        }
    }

    private void PullClipboardFromHost(string host)
    {
        log.LogDebug("Pulling clipboard from {Host}", host);
        var payload = MessageSerializer.Encode(MessageKind.ClipboardPull, new ClipboardPullMessage());
        _ = relay.Send([host], payload).AsTask();
    }

    private async Task OnMessageReceived(string sourceHost, MessageKind kind, string json)
    {
        switch (kind)
        {
            case MessageKind.ScreenInfo:
                var info = json.FromSaneJson<ScreenInfoMessage>();
                if (info != null && info.Screens.Count > 0)
                {
                    await _peerState.SetPeerScreens(sourceHost, info.Screens);
                    if (info.Platform.HasValue)
                        await _peerState.SetPeerPlatform(sourceHost, info.Platform.Value);
                    var snapshot = await _peerState.GetPeerScreensSnapshot();
                    var tcs = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                    if (!_commands.Writer.TryWrite(async st =>
                    {
                        try
                        {
                            RebuildLayout(st, snapshot);
                            if (profile.RemoteOnly) await TryEnterRemoteOnly(st);
                            tcs.TrySetResult();
                        }
                        catch (Exception ex) { tcs.TrySetException(ex); }
                    }))
                        break;
                    await tcs.Task;
                    log.LogInformation("Screen info from {Host}: {Count} screen(s)", sourceHost, info.Screens.Count);
                }
                break;
            case MessageKind.SlaveLog:
                var entry = json.FromSaneJson<SlaveLogMessage>();
                if (entry != null) ForwardSlaveLog(sourceHost, entry);
                break;
            case MessageKind.ScreensaverSync:
                break; // master never acts on screensaver sync messages
            case MessageKind.ClipboardPullResponse:
                var clip = json.FromSaneJson<ClipboardPullResponseMessage>();
                if (clip != null)
                {
                    log.LogDebug("Clipboard pull response from {Host}: text={TextLen}, primary={PrimaryLen}, image={ImageLen}",
                        sourceHost, clip.Text?.Length, clip.PrimaryText?.Length, clip.ImagePng?.Length);
                    var clipText = !string.IsNullOrEmpty(clip.Text) && Encoding.UTF8.GetByteCount(clip.Text) <= MaxClipboardBytes ? clip.Text : null;
                    var clipPrimary = !string.IsNullOrEmpty(clip.PrimaryText) && Encoding.UTF8.GetByteCount(clip.PrimaryText) <= MaxClipboardBytes ? clip.PrimaryText : null;
                    var clipImage = clip.ImagePng?.Length <= MaxClipboardBytes ? clip.ImagePng : null;
                    if (clipText == null && !string.IsNullOrEmpty(clip.Text))
                        log.LogWarning("Clipboard pull response from {Host}: text exceeds {Max} bytes, dropping", sourceHost, MaxClipboardBytes);
                    if (clipPrimary == null && !string.IsNullOrEmpty(clip.PrimaryText))
                        log.LogWarning("Clipboard pull response from {Host}: primary text exceeds {Max} bytes, dropping", sourceHost, MaxClipboardBytes);
                    if (clipImage == null && clip.ImagePng != null)
                        log.LogWarning("Clipboard pull response from {Host}: image exceeds {Max} bytes, dropping", sourceHost, MaxClipboardBytes);
                    _lastReceived = new ClipboardSnapshot(clipText, clipPrimary, clipImage);
                    _clipboardSync.SetClipboard(clipText, clipPrimary, clipImage);
                    // if cursor is currently on a remote screen, forward the clipboard to it
                    var tcs = new TaskCompletionSource<string?>(TaskCreationOptions.RunContinuationsAsynchronously);
                    if (_commands.Writer.TryWrite(st =>
                    {
                        tcs.TrySetResult(st.Mouse.CurrentScreen?.Host);
                        return ValueTask.CompletedTask;
                    }))
                    {
                        var activeHost = await tcs.Task;
                        if (activeHost != null)
                            PushClipboardToHost(activeHost);
                    }
                }
                break;
            case MessageKind.FileSelectionResponse:
                {
                    var osdText = _fileTransfer.HandleSelectionResponse(sourceHost, json);
                    var osdPayload = MessageSerializer.Encode(MessageKind.Osd, new OsdMessage(osdText));
                    _ = relay.Send([sourceHost], osdPayload).AsTask();
                    break;
                }
            case var _ when FileTransferService.IsFileTransferMessage(kind):
                {
                    var wasSendingTo = _fileTransfer.IsSendingTo(sourceHost);
                    var wasCoordinating = _fileTransfer.IsCoordinatingTransferTo(sourceHost);
                    var wasReceivingFrom = _fileTransfer.IsReceivingFrom(sourceHost);
                    if (wasSendingTo || wasCoordinating)
                    {
                        if (kind == MessageKind.FileTransferAccepted)
                            SendOsd(sourceHost, "Pasted!");
                        else if (kind == MessageKind.FileTransferAbort)
                        {
                            var abort = json.FromSaneJson<FileTransferAbortMessage>();
                            if (abort?.Reason == FileTransferService.ReasonNoFolder)
                                SendOsd(sourceHost, "Invalid paste target");
                        }
                    }
                    await _fileTransfer.OnMessageAsync(sourceHost, kind, json, relay);
                    if (wasReceivingFrom && kind == MessageKind.FileTransferDone)
                        osd.Show("Pasted!");
                    break;
                }
            default:
                log.LogDebug("Unhandled message kind {Kind} from {Host}", kind, sourceHost);
                break;
        }
    }

    // routes OSD to slave when cursor is remote, otherwise shows locally on master
    private void ShowOsd(LocalMasterState st, string message)
    {
        if (st.Mouse.IsOnVirtualScreen && st.Mouse.CurrentScreen != null)
        {
            var payload = MessageSerializer.Encode(MessageKind.Osd, new OsdMessage(message));
            _ = relay.Send([st.Mouse.CurrentScreen.Host], payload).AsTask();
        }
        else
            osd.Show(message);
    }

    // sends OSD to a specific known host (file transfer outcomes, etc.)
    private void SendOsd(string targetHost, string message)
    {
        if (targetHost.EqualsIgnoreCase(profile.Name))
            osd.Show(message);
        else
        {
            var payload = MessageSerializer.Encode(MessageKind.Osd, new OsdMessage(message));
            _ = relay.Send([targetHost], payload).AsTask();
        }
    }

    private void ForwardSlaveLog(string sourceHost, SlaveLogMessage entry)
    {
        var category = $"slave:{sourceHost}/{entry.Category}";
        var logger = _peerState.GetOrCreateSlaveLogger(category, loggerFactory);

        var level = (LogLevel)entry.Level;
        // ReSharper disable TemplateIsNotCompileTimeConstantProblem
        if (entry.Exception != null)
            logger.Log(level, "{Message}\n{Exception}", entry.Message, entry.Exception);
        else
            logger.Log(level, "{Message}", entry.Message);
        // ReSharper restore TemplateIsNotCompileTimeConstantProblem
    }

    // rebuilds screens/layout from localScreens/peerScreens; must be called from consumer
    private void RebuildLayout(LocalMasterState st, Dictionary<string, List<ScreenInfoEntry>> peerScreens)
    {
        if (!profile.RemoteOnly && st.ActiveLocalScreen == null) return;

        var newScreens = BuildAllScreens(st.LocalScreens);
        ApplyPeerScreenSizes(peerScreens, newScreens);
        var newLayout = new ScreenLayout(newScreens, profile.Hosts, profile.DeadCorners, BuildScaleMap(st.LocalScreenEntries, peerScreens), log);
        st.Screens = newScreens;
        st.Layout = newLayout;
        st.ActiveLocalScreen = st.ActiveLocalScreen == null ? null
            : st.LocalScreens.FirstOrDefault(s => s.Name.EqualsIgnoreCase(st.ActiveLocalScreen.Name)) ?? st.LocalScreens.FirstOrDefault() ?? st.ActiveLocalScreen;

        // prune stale relative-mode entries for screens that no longer exist
        var validNames = new HashSet<string>(st.Screens.Select(s => s.Name), StringComparer.OrdinalIgnoreCase);
        foreach (var key in st.RelativeMouseScreens.Keys.Where(k => !validNames.Contains(k)).ToList())
            st.RelativeMouseScreens.Remove(key);

        // if the cursor is on a remote screen whose dims changed, update it
        if (st.Mouse.IsOnVirtualScreen && st.Mouse.CurrentScreen != null)
        {
            var refreshed = st.Screens.FirstOrDefault(s => s.Name.EqualsIgnoreCase(st.Mouse.CurrentScreen.Name));
            if (refreshed != null && refreshed != st.Mouse.CurrentScreen)
            {
                var remoteInfo = GetRemoteScreensAndScales(st.Screens, peerScreens, refreshed);
                st.Mouse.EnterScreen(refreshed, remoteInfo.Screens, (int)st.Mouse.X, (int)st.Mouse.Y,
                    remoteInfo.ScaleMap.GetValueOrDefault(refreshed.Name, 1.0m), remoteInfo.ScaleMap, remoteInfo.RelativeScaleMap);
            }
        }
    }

    private static Dictionary<string, decimal> BuildScaleMap(
        List<ScreenInfoEntry> localEntries, Dictionary<string, List<ScreenInfoEntry>> peerScreens)
    {
        var map = new Dictionary<string, decimal>(StringComparer.OrdinalIgnoreCase);
        foreach (var e in localEntries)
            map[e.Name] = e.MouseScale;
        foreach (var entries in peerScreens.Values)
            foreach (var e in entries)
                map[e.Name] = e.MouseScale;
        return map;
    }

    // replaces per-host placeholders with actual per-screen entries from ScreenInfo; must be called from consumer
    private static void ApplyPeerScreenSizes(Dictionary<string, List<ScreenInfoEntry>> peerScreens, List<ScreenRect> screens)
    {
        for (var i = screens.Count - 1; i >= 0; i--)
        {
            var screen = screens[i];
            if (!screen.IsLocal && peerScreens.TryGetValue(screen.Host, out var entries))
            {
                screens.RemoveAt(i);
                // insert in reverse so final order matches ScreenInfo order
                for (var j = entries.Count - 1; j >= 0; j--)
                {
                    var e = entries[j];
                    screens.Insert(i, new ScreenRect(e.Name, screen.Host, e.X, e.Y, e.Width, e.Height, IsLocal: false));
                }
            }
        }
    }

    private static decimal GetRemoteScale(Dictionary<string, List<ScreenInfoEntry>> peerScreens, ScreenRect screen)
    {
        if (peerScreens.TryGetValue(screen.Host, out var entries))
        {
            var entry = entries.FirstOrDefault(e => e.Name.EqualsIgnoreCase(screen.Name));
            if (entry != null) return entry.MouseScale;
        }
        return 1.0m;
    }

    private static decimal? GetRemoteRelativeScale(Dictionary<string, List<ScreenInfoEntry>> peerScreens, ScreenRect screen)
    {
        if (peerScreens.TryGetValue(screen.Host, out var entries))
        {
            var entry = entries.FirstOrDefault(e => e.Name.EqualsIgnoreCase(screen.Name));
            if (entry != null) return entry.RelativeMouseScale;
        }
        return null;
    }

    // builds remoteScreens list + scaleMaps for a given destination host; used when entering a remote screen
    private static RemoteScreenInfo GetRemoteScreensAndScales(
        List<ScreenRect> allScreens, Dictionary<string, List<ScreenInfoEntry>> peerScreens, ScreenRect target)
    {
        var screens = allScreens.Where(s => !s.IsLocal && s.Host.EqualsIgnoreCase(target.Host)).ToList();
        var scaleMap = screens.ToDictionary(s => s.Name, s => GetRemoteScale(peerScreens, s), StringComparer.OrdinalIgnoreCase);
        var relativeScaleMap = screens.ToDictionary(s => s.Name, s => GetRemoteRelativeScale(peerScreens, s), StringComparer.OrdinalIgnoreCase);
        return new RemoteScreenInfo(screens, scaleMap, relativeScaleMap);
    }

    private void OnKeyEvent(KeyEvent keyEvent)
    {
        var label = keyEvent.Character.HasValue ? $" '{keyEvent.Character}'" : keyEvent.Key.HasValue ? $" {keyEvent.Key}" : "";

        _commands.Writer.TryWrite(async st =>
        {
            if (st.Mouse.IsOnVirtualScreen)
                log.LogDebug("Key: {Type}{Label} mods={Modifiers}", keyEvent.Type, label, keyEvent.Modifiers);

            // consume both KeyDown and KeyUp for hotkeys so the slave never sees either half
            var hotkeyConsumed = (keyEvent.Modifiers & LockHotkey) == LockHotkey && keyEvent.Character is 'l' or 'm' or 'c' or 'v' or 'z';
            if (hotkeyConsumed && keyEvent.Type == KeyEventType.KeyDown)
            {
                if (keyEvent.Character == 'l')
                {
                    st.LockedToScreen = !st.LockedToScreen;
                    ShowOsd(st, st.LockedToScreen ? "Mouse lock: On" : "Mouse lock: Off");
                    if (profile.RemoteOnly)
                    {
                        if (st.LockedToScreen)
                        {
                            log.LogInformation("Remote lock: locked to remote");
                            await TryEnterRemoteOnly(st);
                        }
                        else
                        {
                            log.LogInformation("Remote lock: unlocked (local)");
                            if (st.Mouse.IsOnVirtualScreen && st.Mouse.CurrentScreen != null)
                            {
                                var leavingHost = st.Mouse.CurrentScreen.Host;
                                FlushMouseDelta(st);
                                st.Mouse.LeaveScreen();
                                platform.IsOnVirtualScreen = false;
                                await platform.ShowCursor();
                                var payload = MessageSerializer.Encode(MessageKind.LeaveScreen, new LeaveScreenMessage());
                                _ = relay.Send([leavingHost], payload).AsTask();
                                PullClipboardFromHost(leavingHost);
                            }
                        }
                    }
                    else
                        log.LogInformation("Screen lock: {State}", st.LockedToScreen ? "locked" : "unlocked");
                }
                else if (keyEvent.Character == 'm' && st.Mouse.IsOnVirtualScreen && st.Mouse.CurrentScreen != null)
                {
                    var screenName = st.Mouse.CurrentScreen.Name;
                    var isNowRelative = !st.RelativeMouseScreens.GetValueOrDefault(screenName);
                    st.RelativeMouseScreens[screenName] = isNowRelative;
                    log.LogInformation("Mouse mode for '{Screen}': {Mode}", screenName, isNowRelative ? "relative" : "absolute");
                    ShowOsd(st, isNowRelative ? "Relative mouse: On" : "Relative mouse: Off");
                }
                else if (keyEvent.Character == 'c')
                {
                    if (!st.Mouse.IsOnVirtualScreen)
                    {
                        if (!_selectionDetector.IsFileTransferSupported)
                        {
                            log.LogInformation("Copy hotkey: file transfer not supported on this platform");
                            ShowOsd(st, "Action not supported");
                        }
                        else
                        {
                            var result = _selectionDetector.GetSelectedPaths();
                            if (!result.FileManagerFocused)
                            {
                                log.LogInformation("Copy hotkey: {Name} is not focused", _selectionDetector.FileManagerName);
                                ShowOsd(st, $"{_selectionDetector.FileManagerName} is not focused");
                            }
                            else if (result.Paths != null)
                            {
                                log.LogInformation("Copy hotkey: {Count} file(s) selected locally: {Paths}", result.Paths.Count, string.Join(", ", result.Paths));
                                _fileTransfer.SetCopyBuffer(profile.Name, result.Paths);
                                var n = result.Paths.Count;
                                ShowOsd(st, $"{n} {(n == 1 ? "item" : "items")} copied");
                            }
                            else
                            {
                                log.LogInformation("Copy hotkey: no files selected locally");
                                _fileTransfer.ClearCopyBuffer();
                                ShowOsd(st, "0 items selected");
                            }
                        }
                    }
                    else if (st.Mouse.CurrentScreen != null && relay.IsConnected)
                    {
                        log.LogInformation("Copy hotkey: querying file selection on {Host}", st.Mouse.CurrentScreen.Host);
                        var queryPayload = MessageSerializer.Encode(MessageKind.FileSelectionQuery, new FileSelectionQueryMessage());
                        _ = relay.Send([st.Mouse.CurrentScreen.Host], queryPayload).AsTask();
                    }
                }
                else if (keyEvent.Character == 'z' && st.Mouse.IsOnVirtualScreen && st.Mouse.CurrentScreen != null)
                {
                    log.LogInformation("Mission Control hotkey: sending to {Host}", st.Mouse.CurrentScreen.Host);
                    var host = st.Mouse.CurrentScreen.Host;
                    _ = relay.Send([host], MessageSerializer.Encode(MessageKind.KeyEvent, new KeyEventMessage(KeyEventType.KeyDown, KeyModifiers.None, null, SpecialKey.MissionControl))).AsTask();
                    _ = relay.Send([host], MessageSerializer.Encode(MessageKind.KeyEvent, new KeyEventMessage(KeyEventType.KeyUp, KeyModifiers.None, null, SpecialKey.MissionControl))).AsTask();
                }
                else if (keyEvent.Character == 'v')
                {
                    if (!_selectionDetector.IsFileTransferSupported)
                    {
                        log.LogInformation("Paste hotkey: file transfer not supported on this platform");
                        ShowOsd(st, "Action not supported");
                    }
                    else if (_fileTransfer.GetCopyBuffer() is { } copyBuffer)
                    {
                        var targetHost = st.Mouse.IsOnVirtualScreen && st.Mouse.CurrentScreen != null
                            ? st.Mouse.CurrentScreen.Host
                            : profile.Name;
                        if (string.Equals(copyBuffer.SourceHost, targetHost, StringComparison.OrdinalIgnoreCase))
                        {
                            log.LogInformation("Paste hotkey: source and target are the same host ({Host}), nothing to do", targetHost);
                            ShowOsd(st, "Invalid paste target");
                        }
                        else
                        {
                            log.LogInformation("Paste hotkey: {Count} file(s) from {Source} → {Target}", copyBuffer.Paths.Length, copyBuffer.SourceHost, targetHost);
                            if (!_fileTransfer.InitiatePaste(copyBuffer, targetHost, profile.Name, relay))
                                SendOsd(targetHost, "Invalid paste target");
                        }
                    }
                    else
                    {
                        log.LogInformation("Paste hotkey: copy buffer is empty");
                        ShowOsd(st, "Nothing to paste");
                    }
                }
            }

            if (!hotkeyConsumed && st.Mouse.IsOnVirtualScreen && relay.IsConnected)
            {
                // include repeat settings on the first KeyDown so the slave can generate local repeats
                int? repeatDelay = null, repeatRate = null;
                if (keyEvent.Type == KeyEventType.KeyDown)
                {
                    repeatDelay = _repeatDelayMs;
                    repeatRate = _repeatRateMs;
                }
                ForwardToVirtualScreen(st, MessageKind.KeyEvent, new KeyEventMessage(keyEvent.Type, keyEvent.Modifiers, keyEvent.Character, RemapKey(keyEvent.Key), repeatDelay, repeatRate));
            }
        });
    }

    private void OnMouseButton(MouseButtonEvent e)
    {
        _commands.Writer.TryWrite(st =>
        {
            if (st.Mouse.IsOnVirtualScreen && relay.IsConnected)
            {
                log.LogDebug("Mouse: {Type} {Button}", e.IsPressed ? "down" : "up", e.Button);
                ForwardToVirtualScreen(st, MessageKind.MouseButton, new MouseButtonMessage(e.Button, e.IsPressed));
            }
            return ValueTask.CompletedTask;
        });
    }

    private void OnMouseScroll(MouseScrollEvent e)
    {
        _commands.Writer.TryWrite(st =>
        {
            if (st.Mouse.IsOnVirtualScreen && relay.IsConnected)
            {
                log.LogDebug("Scroll: x={X} y={Y}", e.XDelta, e.YDelta);
                ForwardToVirtualScreen(st, MessageKind.MouseScroll, new MouseScrollMessage(e.XDelta, e.YDelta));
            }
            return ValueTask.CompletedTask;
        });
    }

    private void SendMousePosition(LocalMasterState st, long now)
    {
        if (!relay.IsConnected || st.Mouse.CurrentScreen == null) return;

        var screen = st.Mouse.CurrentScreen;
        byte[] payload;

        if (st.RelativeMouseScreens.GetValueOrDefault(screen.Name))
        {
            // relative mode: send accumulated delta, preserve sub-pixel remainders
            var intDx = (int)st.PendingDx;
            var intDy = (int)st.PendingDy;
            if (intDx == 0 && intDy == 0) return;
            st.PendingDx -= intDx;
            st.PendingDy -= intDy;
            payload = MessageSerializer.Encode(MessageKind.MouseMoveDelta, new MouseMoveDeltaMessage(intDx, intDy));
        }
        else
        {
            // absolute mode: send current virtual position, discard accumulated deltas
            st.PendingDx = 0;
            st.PendingDy = 0;
            payload = MessageSerializer.Encode(MessageKind.MouseMove, new MouseMoveMessage(screen.Name, (int)st.Mouse.X, (int)st.Mouse.Y));
        }

        st.LastMouseSendTick = now;
        _ = relay.Send([screen.Host], payload).AsTask();
    }

    // flush pending state before leaving a virtual screen.
    // relative mode: send any accumulated delta.
    // absolute mode: always send current position so slave cursor doesn't lag at exit point.
    private void FlushMouseDelta(LocalMasterState st)
    {
        if (!relay.IsConnected || st.Mouse.CurrentScreen == null) return;
        var screen = st.Mouse.CurrentScreen;
        var isRelative = st.RelativeMouseScreens.GetValueOrDefault(screen.Name);
        if (isRelative && st.PendingDx == 0 && st.PendingDy == 0) return;
        SendMousePosition(st, Environment.TickCount64);
    }

    // remap Home/End to platform-independent line-nav keys when master is not Mac.
    private static SpecialKey? RemapKey(SpecialKey? key) => key switch
    {
        SpecialKey.Home when !OperatingSystem.IsMacOS() => SpecialKey.MoveToBeginningOfLine,
        SpecialKey.End when !OperatingSystem.IsMacOS() => SpecialKey.MoveToEndOfLine,
        _ => key,
    };

    private void ForwardToVirtualScreen<T>(LocalMasterState st, MessageKind kind, T message)
    {
        var target = st.Mouse.CurrentScreen?.Host;
        if (target == null) return;
        var payload = MessageSerializer.Encode(kind, message);
        _ = relay.Send([target], payload).AsTask();
    }

    private void OnMouseMove(double x, double y)
    {
        _commands.Writer.TryWrite(async st =>
        {
            if (st.Layout is null || st.ActiveLocalScreen is null) return;
            if (!st.Mouse.IsOnVirtualScreen)
                await HandleRealScreenMove(st, x, y);
            else
                await HandleVirtualScreenMove(st, x, y);
        });
    }

    private async ValueTask HandleRealScreenMove(LocalMasterState st, double x, double y)
    {
        // track which local screen the cursor is on
        var screen = FindLocalScreenAt(st, (int)x, (int)y) ?? st.ActiveLocalScreen!;
        if (screen != st.ActiveLocalScreen)
        {
            st.ActiveLocalScreen = screen;
            UpdateWarpPoint(st, screen);
        }

        if (st.LockedToScreen) return;

        var localX = (int)x - screen.X;
        var localY = (int)y - screen.Y;
        var hit = st.Layout!.DetectEdgeExit(screen, localX, localY);
        if (hit is null) return;
        if (!relay.IsConnected) return;

        var peerScreens = await _peerState.GetPeerScreensSnapshot();
        var scale = GetRemoteScale(peerScreens, hit.Destination);
        var remoteInfo = GetRemoteScreensAndScales(st.Screens, peerScreens, hit.Destination);

        // block edge crossing while any button is held
        if (platform.AnyMouseButtonHeld()) return;

        await platform.HideCursor();
        platform.IsOnVirtualScreen = true;
        st.Mouse.EnterScreen(hit.Destination, remoteInfo.Screens, hit.EntryX, hit.EntryY, scale, remoteInfo.ScaleMap, remoteInfo.RelativeScaleMap);
        st.PendingDx = 0;
        st.PendingDy = 0;
        // warp immediately so pre-queued events compute large dx → caught by bogus filter
        platform.WarpCursor(st.WarpX, st.WarpY);
        st.LastWarpX = st.WarpX;
        st.LastWarpY = st.WarpY;
        st.PendingWarpCount = 1;
        log.LogInformation("Entered remote screen '{Name}' → ({X}, {Y})", hit.Destination.Name, hit.EntryX, hit.EntryY);

        if (relay.IsConnected)
        {
            var payload = MessageSerializer.Encode(MessageKind.EnterScreen, new EnterScreenMessage(hit.Destination.Name, hit.EntryX, hit.EntryY, hit.Destination.Width, hit.Destination.Height));
            _ = relay.Send([hit.Destination.Host], payload).AsTask();
            PushClipboardToHost(hit.Destination.Host);
        }
    }

    private async ValueTask HandleVirtualScreenMove(LocalMasterState st, double x, double y)
    {
        // filter the synthetic warp event generated by WarpCursor to prevent a false delta
        if (st.PendingWarpCount > 0 && (int)x == st.WarpX && (int)y == st.WarpY)
        {
            st.PendingWarpCount--;
            st.LastWarpX = st.WarpX;
            st.LastWarpY = st.WarpY;
            return;
        }

        var dx = x - st.LastWarpX;
        var dy = y - st.LastWarpY;

        // zero-delta filter
        if (dx == 0 && dy == 0) return;

        // bogus filter: drop delta that looks like a pre-warp or edge artifact.
        // position tracking is updated AFTER this check — a bogus-filtered event must not
        // anchor LastWarpX here, or the very next event would see a small negative delta
        // and incorrectly exit the virtual screen.
        if (Math.Abs(dx) > st.HalfW - 10 || Math.Abs(dy) > st.HalfH - 10) return;

        // track event position so burst events compute correct per-event deltas
        st.LastWarpX = x;
        st.LastWarpY = y;

        var prevScreen = st.Mouse.ApplyDelta(dx, dy);
        if (prevScreen != null)
            HandleIntraHostTransition(st);
        else
        {
            // same screen — accumulate scaled deltas for throttle
            var isRelative = st.RelativeMouseScreens.GetValueOrDefault(st.Mouse.CurrentScreen!.Name);
            var scale = isRelative ? (double)(st.Mouse.RelativeMouseScale ?? st.Mouse.MouseScale) : (double)st.Mouse.MouseScale;
            st.PendingDx += dx * scale;
            st.PendingDy += dy * scale;
        }

        var now = Environment.TickCount64;
        if (now - st.LastVirtualLogTick >= 100)
        {
            st.LastVirtualLogTick = now;
            if (st.Mouse.CurrentScreen != null && st.RelativeMouseScreens.GetValueOrDefault(st.Mouse.CurrentScreen.Name))
                log.LogDebug("Mouse: ({X}, {Y})  Offset: ({DX}, {DY})", (int)st.Mouse.X, (int)st.Mouse.Y, (int)st.PendingDx, (int)st.PendingDy);
            else
                log.LogDebug("Mouse: ({X}, {Y})", (int)st.Mouse.X, (int)st.Mouse.Y);
        }

        // check edge exit
        {
            var virtualScreen = st.Mouse.CurrentScreen!;
            var hit = st.Layout!.DetectEdgeExit(virtualScreen, (int)st.Mouse.X, (int)st.Mouse.Y);
            if (hit is not null)
            {
                if (!hit.Destination.IsLocal)
                {
                    if (profile.RemoteOnly || !st.LockedToScreen)
                    {
                        if (!platform.AnyMouseButtonHeld())
                        {
                            var leavingScreen = st.Mouse.CurrentScreen;
                            FlushMouseDelta(st);
                            var peerScreens = await _peerState.GetPeerScreensSnapshot();
                            var remoteInfo = GetRemoteScreensAndScales(st.Screens, peerScreens, hit.Destination);
                            var scale = remoteInfo.ScaleMap.GetValueOrDefault(hit.Destination.Name, 1.0m);
                            st.Mouse.EnterScreen(hit.Destination, remoteInfo.Screens, hit.EntryX, hit.EntryY, scale, remoteInfo.ScaleMap, remoteInfo.RelativeScaleMap);
                            st.PendingDx = 0;
                            st.PendingDy = 0;
                            log.LogInformation("Switched to remote screen '{Name}' → ({X}, {Y})", hit.Destination.Name, hit.EntryX, hit.EntryY);

                            if (relay.IsConnected)
                            {
                                if (leavingScreen != null && leavingScreen.Host != hit.Destination.Host)
                                {
                                    var leavePayload = MessageSerializer.Encode(MessageKind.LeaveScreen, new LeaveScreenMessage());
                                    _ = relay.Send([leavingScreen.Host], leavePayload).AsTask();
                                    PullClipboardFromHost(leavingScreen.Host);
                                }
                                var enterPayload = MessageSerializer.Encode(MessageKind.EnterScreen,
                                    new EnterScreenMessage(hit.Destination.Name, hit.EntryX, hit.EntryY, hit.Destination.Width, hit.Destination.Height));
                                _ = relay.Send([hit.Destination.Host], enterPayload).AsTask();
                                PushClipboardToHost(hit.Destination.Host);
                            }
                            return;
                        }
                    }
                }
                else if (!st.LockedToScreen && !profile.RemoteOnly)
                {
                    if (!platform.AnyMouseButtonHeld())
                    {
                        var targetScreen = hit.Destination;

                        FlushMouseDelta(st);

                        var globalX = targetScreen.X + hit.EntryX;
                        var globalY = targetScreen.Y + hit.EntryY;
                        var leavingScreen = st.Mouse.CurrentScreen;
                        st.Mouse.LeaveScreen();
                        ReturnToLocalScreen(globalX, globalY);
                        await platform.ShowCursor();
                        st.ActiveLocalScreen = targetScreen;
                        UpdateWarpPoint(st, targetScreen);
                        log.LogInformation("Returned to local screen ← ({X}, {Y})", globalX, globalY);

                        if (relay.IsConnected && leavingScreen != null)
                        {
                            var payload = MessageSerializer.Encode(MessageKind.LeaveScreen, new LeaveScreenMessage());
                            _ = relay.Send([leavingScreen.Host], payload).AsTask();
                            PullClipboardFromHost(leavingScreen.Host);
                        }
                        return;
                    }
                }
            }
        }

        // throttle mouse sends to MaxMouseHz
        if (now - st.LastMouseSendTick >= MinMouseIntervalMs)
            SendMousePosition(st, now);

        // warp to center on every event; track count so we can filter the synthetic event
        platform.WarpCursor(st.WarpX, st.WarpY);
        st.PendingWarpCount++;
    }

    private void HandleIntraHostTransition(LocalMasterState st)
    {
        st.PendingDx = 0;
        st.PendingDy = 0;
        if (!relay.IsConnected || st.Mouse.CurrentScreen == null) return;
        var s = st.Mouse.CurrentScreen;
        var payload = MessageSerializer.Encode(MessageKind.EnterScreen,
            new EnterScreenMessage(s.Name, (int)st.Mouse.X, (int)st.Mouse.Y, s.Width, s.Height));
        _ = relay.Send([s.Host], payload).AsTask();
    }

    private void ReturnToLocalScreen(int x, int y)
    {
        platform.IsOnVirtualScreen = false;
        platform.WarpCursor(x, y);
    }

    // shared in-consumer cleanup for peer-disconnect / relay-disconnect / screensaver snap-back.
    // returns the host we left (null if already on local screen).
    private static string? LeaveVirtualScreen(LocalMasterState st, out int warpX, out int warpY)
    {
        warpX = warpY = 0;
        if (!st.Mouse.IsOnVirtualScreen || st.Mouse.CurrentScreen == null) return null;
        var host = st.Mouse.CurrentScreen.Host;
        st.Mouse.LeaveScreen();
        st.PendingDx = 0;
        st.PendingDy = 0;
        warpX = st.WarpX;
        warpY = st.WarpY;
        return host;
    }

    // enters remote-only mode targeting the first remote screen with known dimensions.
    // must be called from consumer. no-op if already on virtual screen or no screen ready yet.
    private async ValueTask TryEnterRemoteOnly(LocalMasterState st)
    {
        if (st.Mouse.IsOnVirtualScreen) return;
        if (!st.LockedToScreen) return;  // user explicitly unlocked to local — don't auto-re-enter
        var target = st.Screens.FirstOrDefault(s => !s.IsLocal && s.Width > 0);
        if (target == null) return;
        if (!relay.IsConnected) return;

        var peerScreens = await _peerState.GetPeerScreensSnapshot();
        var remoteInfo = GetRemoteScreensAndScales(st.Screens, peerScreens, target);
        var scale = remoteInfo.ScaleMap.GetValueOrDefault(target.Name, 1.0m);
        var entryX = target.Width / 2;
        var entryY = target.Height / 2;

        await platform.HideCursor();
        platform.IsOnVirtualScreen = true;
        st.Mouse.EnterScreen(target, remoteInfo.Screens, entryX, entryY, scale, remoteInfo.ScaleMap, remoteInfo.RelativeScaleMap);
        st.PendingDx = 0;
        st.PendingDy = 0;
        st.LockedToScreen = true;
        st.PendingWarpCount = 0;
        log.LogInformation("Remote-only: entered '{Name}' → ({X}, {Y})", target.Name, entryX, entryY);

        var payload = MessageSerializer.Encode(MessageKind.EnterScreen, new EnterScreenMessage(target.Name, entryX, entryY, target.Width, target.Height));
        _ = relay.Send([target.Host], payload).AsTask();
        PushClipboardToHost(target.Host);
    }

    // handles raw mouse deltas from evdev (remote-only/console mode).
    // feeds directly into VirtualMouseState — no warp-point math needed.
    private void OnMouseDelta(double dx, double dy)
    {
        _commands.Writer.TryWrite(async st =>
        {
            if (!st.Mouse.IsOnVirtualScreen) return;

            var leavingScreen = st.Mouse.CurrentScreen!;
            var prevScreen = st.Mouse.ApplyDelta(dx, dy);
            if (prevScreen != null)
            {
                HandleIntraHostTransition(st);
                return;
            }

            // check for cross-host edge exit
            var hit = st.Layout?.DetectEdgeExit(st.Mouse.CurrentScreen!, (int)st.Mouse.X, (int)st.Mouse.Y);
            if (hit is not null && !hit.Destination.IsLocal && relay.IsConnected && !platform.AnyMouseButtonHeld())
            {
                await HandleEvdevCrossHostTransitionAsync(st, leavingScreen, hit);
                return;
            }

            var isRelative = st.RelativeMouseScreens.GetValueOrDefault(st.Mouse.CurrentScreen!.Name);
            var scale = isRelative ? (double)(st.Mouse.RelativeMouseScale ?? st.Mouse.MouseScale) : (double)st.Mouse.MouseScale;
            st.PendingDx += dx * scale;
            st.PendingDy += dy * scale;

            var now = Environment.TickCount64;
            if (now - st.LastMouseSendTick >= MinMouseIntervalMs)
                SendMousePosition(st, now);
        });
    }

    // evdev cross-host transitions; called from consumer, so st access is safe
    private async ValueTask HandleEvdevCrossHostTransitionAsync(LocalMasterState st, ScreenRect leavingScreen, EdgeHit hit)
    {
        FlushMouseDelta(st);
        var peerScreens = await _peerState.GetPeerScreensSnapshot();
        var remoteInfo = GetRemoteScreensAndScales(st.Screens, peerScreens, hit.Destination);
        var scale = remoteInfo.ScaleMap.GetValueOrDefault(hit.Destination.Name, 1.0m);
        st.Mouse.EnterScreen(hit.Destination, remoteInfo.Screens, hit.EntryX, hit.EntryY, scale, remoteInfo.ScaleMap, remoteInfo.RelativeScaleMap);
        st.PendingDx = 0;
        st.PendingDy = 0;
        log.LogInformation("Switched to remote screen '{Name}' → ({X}, {Y})", hit.Destination.Name, hit.EntryX, hit.EntryY);

        if (leavingScreen.Host != hit.Destination.Host)
        {
            var leavePayload = MessageSerializer.Encode(MessageKind.LeaveScreen, new LeaveScreenMessage());
            _ = relay.Send([leavingScreen.Host], leavePayload).AsTask();
            PullClipboardFromHost(leavingScreen.Host);
        }
        var crossPayload = MessageSerializer.Encode(MessageKind.EnterScreen,
            new EnterScreenMessage(hit.Destination.Name, hit.EntryX, hit.EntryY, hit.Destination.Width, hit.Destination.Height));
        _ = relay.Send([hit.Destination.Host], crossPayload).AsTask();
        PushClipboardToHost(hit.Destination.Host);
    }

    private static void UpdateWarpPoint(LocalMasterState st, ScreenRect screen)
    {
        st.HalfW = screen.Width / 2;
        st.HalfH = screen.Height / 2;
        st.WarpX = screen.X + st.HalfW;
        st.WarpY = screen.Y + st.HalfH;
    }

    private static ScreenRect? FindLocalScreenAt(LocalMasterState st, int x, int y) =>
        st.LocalScreens.FirstOrDefault(s => s.Contains(x, y));

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    private void LogDetectedScreens(List<ScreenRect> detected)
    {
        log.LogInformation("Detected {Count} local screen(s):", detected.Count);
        for (var i = 0; i < detected.Count; i++)
            if (detected[i].Identity != null)
                log.LogInformation("  Screen {I}: {Json}", i, JsonSerializer.Serialize(detected[i].Identity, JsonOptions));
    }

    // combines local screens with placeholder remote screens (Width=0 until ScreenInfo arrives)
    private List<ScreenRect> BuildAllScreens(List<ScreenRect> localScreens)
    {
        var result = new List<ScreenRect>(localScreens);
        foreach (var host in profile.RemoteHosts)
            result.Add(new ScreenRect(host.Name, host.Name, 0, 0, 0, 0, IsLocal: false));
        return result;
    }

    private class LocalMasterState
    {
        public List<ScreenRect> Screens = [];
        public List<ScreenRect> LocalScreens = [];
        public List<ScreenInfoEntry> LocalScreenEntries = [];
        public ScreenRect? ActiveLocalScreen;
        public ScreenLayout? Layout;
        public VirtualMouseState Mouse = new();
        public int WarpX, WarpY, HalfW, HalfH;
        public double LastWarpX, LastWarpY;
        public long LastVirtualLogTick;
        public bool LockedToScreen;

        // per-screen relative mouse mode (true = relative, false/absent = absolute)
        public Dictionary<string, bool> RelativeMouseScreens = new(StringComparer.OrdinalIgnoreCase);

        // screensaver sync: saved cursor location before screensaver snap-back
        public bool ScreensaverActive;
        public string? SavedScreenName;
        public int SavedCursorX, SavedCursorY;

        // 120Hz throttle: accumulated deltas and last send time
        public long LastMouseSendTick;
        public double PendingDx;
        public double PendingDy;

        // number of OS-generated synthetic warp events still in flight
        public int PendingWarpCount;

    }

    private record RemoteScreenInfo(List<ScreenRect> Screens, Dictionary<string, decimal> ScaleMap, Dictionary<string, decimal?> RelativeScaleMap);
}

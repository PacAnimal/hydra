using System.Text.Json;
using System.Text.Json.Serialization;
using Cathedral.Extensions;
using Cathedral.Utils;
using Hydra.Config;
using Hydra.Keyboard;
using Hydra.Mouse;
using Hydra.Platform;
using Hydra.Relay;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Hydra.Screen;

public class ScreenTransitionService(
    IPlatformInput platform,
    HydraConfig config,
    IRelaySender relay,
    IScreenDetector screens,
    ILoggerFactory loggerFactory,
    ILogger<ScreenTransitionService> log,
    IScreenSaverSync screenSaverSync,
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
    private long _lastRepeatSettingsTick;

    private readonly IWorldState _peerState = peerState ?? new WorldState();
    private readonly SemaphoreSlimValue<LocalMasterState> _state = new(new LocalMasterState(), disposeValue: false);
    private CancellationTokenSource? _pollCts;
    private readonly IScreenSaverSync _screenSaverSync = screenSaverSync;

    public async Task StartAsync(CancellationToken cancellationToken)
    {
        if (!platform.IsAccessibilityTrusted())
        {
            log.LogError("Input hook permission not granted. On macOS: grant access in System Settings > Privacy & Security > Accessibility. Then restart Hydra.");
            return;
        }

        log.LogInformation("Host: {Name}", config.ResolvedName);

        if (config.LocalHost == null && config.Hosts.Count > 0)
        {
            log.LogError("Host '{Name}' is not listed in the config hosts — add it to the hosts list.", config.ResolvedName);
            return;
        }

        var snapshot = await screens.Get(cancellationToken);
        LogDetectedScreens(snapshot.Screens);

        using (var s = await _state.WaitForDisposable(cancellationToken))
        {
            var st = s.Value;
            st.LocalScreens = snapshot.Screens;
            st.ActiveLocalScreen = st.LocalScreens.FirstOrDefault();

            if (st.ActiveLocalScreen == null)
            {
                log.LogError("No local screens detected.");
                return;
            }

            UpdateWarpPoint(st, st.ActiveLocalScreen);
            st.Screens = BuildAllScreens(st.LocalScreens, config);
            st.Layout = new ScreenLayout(st.Screens, config.Hosts, config.DeadCorners, log);

            foreach (var remote in st.Screens.Where(r => !r.IsLocal))
                log.LogInformation("Remote screen '{Name}': waiting for peer", remote.Name);
        }

        var (delayMs, rateMs) = platform.GetKeyRepeatSettings();
        _repeatDelayMs = delayMs;
        _repeatRateMs = rateMs;
        _lastRepeatSettingsTick = Environment.TickCount64;

        relay.PeersChanged += OnPeersChanged;
        relay.MessageReceived += OnMessageReceived;
        relay.Disconnected += OnRelayDisconnected;
        screens.ScreensChanged += OnScreensChanged;

        platform.StartEventTap((x, y) => OnMouseMove(x, y), OnKeyEvent, OnMouseButton, OnMouseScroll);

        if (config.SyncScreensaver)
            _screenSaverSync.StartWatching(OnScreensaverActivated, OnScreensaverDeactivated);

        _pollCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
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

        if (config.SyncScreensaver)
            _screenSaverSync.StopWatching();
        platform.StopEventTap();

#pragma warning disable CA2016 // intentionally not propagated — cleanup must always run
        using var s = await _state.WaitForDisposable();
#pragma warning restore CA2016
        var st = s.Value;
        if (st.Mouse.IsOnVirtualScreen || st.PendingCursorShow)
        {
            st.PendingCursorShow = false;
            platform.IsOnVirtualScreen = false;
            platform.ShowCursor();
        }
    }

    private void OnScreensChanged(LocalScreenSnapshot snapshot)
    {
        log.LogInformation("Screen configuration changed — rebuilding layout");
        LogDetectedScreens(snapshot.Screens);
        var newScreens = BuildAllScreens(snapshot.Screens, config);
        var peerScreens = AsyncHelper.RunSync(() => _peerState.GetPeerScreensSnapshot().AsTask());

        using var s = AsyncHelper.RunSync(() => _state.WaitForDisposable());
        var st = s.Value;
        ApplyPeerScreenSizes(peerScreens, newScreens);
        st.LocalScreens = snapshot.Screens;
        st.Screens = newScreens;
        st.Layout = new ScreenLayout(newScreens, config.Hosts, config.DeadCorners, log);

        if (!st.Mouse.IsOnVirtualScreen)
        {
            st.ActiveLocalScreen = st.LocalScreens.FirstOrDefault() ?? st.ActiveLocalScreen;
            if (st.ActiveLocalScreen != null) UpdateWarpPoint(st, st.ActiveLocalScreen);
        }
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
                _lastRepeatSettingsTick = Environment.TickCount64;
            }
        }
        catch (OperationCanceledException) { }
    }

    private void OnPeersChanged(string[] hostNames)
    {
        var current = new HashSet<string>(hostNames, StringComparer.OrdinalIgnoreCase);
        var configuredSlaves = config.RemoteHosts
            .Select(s => s.Name)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        var delta = AsyncHelper.RunSync(() => _peerState.UpdatePeers(current, configuredSlaves).AsTask());

        string? disconnectedHost = null;
        int warpX = 0, warpY = 0;

        using (var s = AsyncHelper.RunSync(() => _state.WaitForDisposable()))
        {
            var st = s.Value;

            // snap back if the peer we're currently on has disconnected
            if (st.Mouse.IsOnVirtualScreen && st.Mouse.CurrentScreen != null)
            {
                var currentHost = st.Mouse.CurrentScreen.Host;
                if (!current.Contains(currentHost))
                {
                    st.Mouse.LeaveScreen();
                    st.PendingDX = 0;
                    st.PendingDY = 0;
                    disconnectedHost = currentHost;
                    warpX = st.WarpX;
                    warpY = st.WarpY;
                    st.PendingCursorShow = false;
                }
            }

            // rebuild layout so departed screens go back to Width=0 (offline)
            if (delta.AnyDeparted) RebuildLayout(st, delta.PeerScreensSnapshot);
        }

        if (disconnectedHost != null)
        {
            ReturnToLocalScreen(warpX, warpY);
            platform.ShowCursor();
            log.LogInformation("Remote peer '{Name}' disconnected — returned to local screen", disconnectedHost);
        }

        // send MasterConfig only to newly appeared peers that are configured as slaves
        foreach (var host in delta.NewPeers)
        {
            var payload = MessageSerializer.Encode(MessageKind.MasterConfig, new { });
            _ = relay.Send([host], payload).AsTask();
            log.LogDebug("Sent MasterConfig to {Host}", host);
        }
    }

    private void OnRelayDisconnected()
    {
        string? disconnectedHost = null;
        int warpX = 0, warpY = 0;

        using (var s = AsyncHelper.RunSync(() => _state.WaitForDisposable()))
        {
            var st = s.Value;
            if (st.Mouse.IsOnVirtualScreen && st.Mouse.CurrentScreen != null)
            {
                disconnectedHost = st.Mouse.CurrentScreen.Host;
                st.Mouse.LeaveScreen();
                st.PendingDX = 0;
                st.PendingDY = 0;
                warpX = st.WarpX;
                warpY = st.WarpY;
                st.PendingCursorShow = false;
            }
        }

        if (disconnectedHost != null)
        {
            ReturnToLocalScreen(warpX, warpY);
            platform.ShowCursor();
            log.LogWarning("Relay disconnected — returned to local screen from '{Host}'", disconnectedHost);
        }
    }

    private void OnScreensaverActivated()
    {
        string? disconnectedHost = null;
        int warpX = 0, warpY = 0;

        using (var s = AsyncHelper.RunSync(() => _state.WaitForDisposable()))
        {
            var st = s.Value;
            if (st.ScreensaverActive) return;
            st.ScreensaverActive = true;

            // save cursor location so we can restore after screensaver dismissal
            if (st.Mouse.IsOnVirtualScreen && st.Mouse.CurrentScreen != null)
            {
                st.SavedScreenName = st.Mouse.CurrentScreen.Name;
                st.SavedCursorX = (int)st.Mouse.X;
                st.SavedCursorY = (int)st.Mouse.Y;

                FlushMouseDelta(st);
                disconnectedHost = st.Mouse.CurrentScreen.Host;
                st.Mouse.LeaveScreen();
                st.PendingDX = 0;
                st.PendingDY = 0;
                warpX = st.WarpX;
                warpY = st.WarpY;
                st.PendingCursorShow = false;
            }
        }

        if (disconnectedHost != null)
        {
            var leavePayload = MessageSerializer.Encode(MessageKind.LeaveScreen, new { });
            _ = relay.Send([disconnectedHost], leavePayload).AsTask();
            ReturnToLocalScreen(warpX, warpY);
            platform.ShowCursor();
        }

        BroadcastScreensaverSync(true);
        log.LogDebug("Screensaver activated — synced to slaves");
    }

    private void OnScreensaverDeactivated()
    {
        string? savedScreen = null;
        int savedX = 0, savedY = 0;

        using (var s = AsyncHelper.RunSync(() => _state.WaitForDisposable()))
        {
            var st = s.Value;
            if (!st.ScreensaverActive) return;
            st.ScreensaverActive = false;
            savedScreen = st.SavedScreenName;
            savedX = st.SavedCursorX;
            savedY = st.SavedCursorY;
            st.SavedScreenName = null;
        }

        BroadcastScreensaverSync(false);
        log.LogDebug("Screensaver deactivated — synced to slaves");

        // best-effort cursor restore: re-enter saved remote screen if still connected and accessible
        if (savedScreen != null && relay.IsConnected)
        {
            using var s = AsyncHelper.RunSync(() => _state.WaitForDisposable());
            var st = s.Value;
            var dest = st.Screens.FirstOrDefault(sc => !sc.IsLocal && sc.Name.EqualsIgnoreCase(savedScreen));
            if (dest != null && dest.Width > 0)
            {
                var peerScreens = AsyncHelper.RunSync(() => _peerState.GetPeerScreensSnapshot().AsTask());
                var remoteInfo = GetRemoteScreensAndScales(st.Screens, peerScreens, dest);
                var scale = GetRemoteScale(peerScreens, dest);
                platform.HideCursor();
                platform.IsOnVirtualScreen = true;
                st.Mouse.EnterScreen(dest, remoteInfo.Screens, savedX, savedY, scale, remoteInfo.ScaleMap);
                st.PendingDX = 0;
                st.PendingDY = 0;
                st.LastWarpX = st.WarpX;
                st.LastWarpY = st.WarpY;
                var enterPayload = MessageSerializer.Encode(MessageKind.EnterScreen,
                    new EnterScreenMessage(dest.Name, savedX, savedY, dest.Width, dest.Height));
                _ = relay.Send([dest.Host], enterPayload).AsTask();
                log.LogDebug("Restored cursor to '{Screen}' after screensaver", savedScreen);
            }
        }
    }

    private void BroadcastScreensaverSync(bool active)
    {
        var peerScreens = AsyncHelper.RunSync(() => _peerState.GetPeerScreensSnapshot().AsTask());
        var hosts = peerScreens.Keys.ToArray();
        if (hosts.Length == 0) return;
        var payload = MessageSerializer.Encode(MessageKind.ScreensaverSync, new ScreensaverSyncMessage(active));
        _ = relay.Send(hosts, payload).AsTask();
    }

    private void OnMessageReceived(string sourceHost, MessageKind kind, string json)
    {
        switch (kind)
        {
            case MessageKind.ScreenInfo:
                var info = json.FromSaneJson<ScreenInfoMessage>();
                if (info != null && info.Screens.Count > 0)
                {
                    AsyncHelper.RunSync(() => _peerState.SetPeerScreens(sourceHost, info.Screens).AsTask());
                    var snapshot = AsyncHelper.RunSync(() => _peerState.GetPeerScreensSnapshot().AsTask());
                    using (var s = AsyncHelper.RunSync(() => _state.WaitForDisposable()))
                        RebuildLayout(s.Value, snapshot);
                    log.LogInformation("Screen info from {Host}: {Count} screen(s)", sourceHost, info.Screens.Count);
                }
                break;
            case MessageKind.SlaveLog:
                var entry = json.FromSaneJson<SlaveLogMessage>();
                if (entry != null) ForwardSlaveLog(sourceHost, entry);
                break;
            case MessageKind.ScreensaverSync:
                break; // master never acts on screensaver sync messages
            default:
                log.LogDebug("Unhandled message kind {Kind} from {Host}", kind, sourceHost);
                break;
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

    // rebuilds screens/layout from localScreens/peerScreens; must be called under lock
    private void RebuildLayout(LocalMasterState st, Dictionary<string, List<ScreenInfoEntry>> peerScreens)
    {
        if (st.ActiveLocalScreen == null) return;

        var newScreens = BuildAllScreens(st.LocalScreens, config);
        ApplyPeerScreenSizes(peerScreens, newScreens);
        var newLayout = new ScreenLayout(newScreens, config.Hosts, config.DeadCorners, log);
        st.Screens = newScreens;
        st.Layout = newLayout;
        st.ActiveLocalScreen = st.LocalScreens.FirstOrDefault(s => s.Name.EqualsIgnoreCase(st.ActiveLocalScreen.Name)) ?? st.LocalScreens.FirstOrDefault() ?? st.ActiveLocalScreen;

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
                st.Mouse.EnterScreen(refreshed, remoteInfo.Screens, (int)st.Mouse.X, (int)st.Mouse.Y, remoteInfo.ScaleMap.GetValueOrDefault(refreshed.Name, 1.0m), remoteInfo.ScaleMap);
            }
        }
    }

    // replaces per-host placeholders with actual per-screen entries from ScreenInfo; must be called under lock
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
            if (entry != null) return entry.Scale;
        }
        return 1.0m;
    }

    // builds remoteScreens list + scaleMap for a given destination host; used when entering a remote screen
    private static RemoteScreenInfo GetRemoteScreensAndScales(
        List<ScreenRect> allScreens, Dictionary<string, List<ScreenInfoEntry>> peerScreens, ScreenRect target)
    {
        var screens = allScreens.Where(s => !s.IsLocal && s.Host.EqualsIgnoreCase(target.Host)).ToList();
        var scaleMap = screens.ToDictionary(s => s.Name, s => GetRemoteScale(peerScreens, s), StringComparer.OrdinalIgnoreCase);
        return new RemoteScreenInfo(screens, scaleMap);
    }

    private void OnKeyEvent(KeyEvent keyEvent)
    {
        var label = keyEvent.Character.HasValue ? $" '{keyEvent.Character}'" : keyEvent.Key.HasValue ? $" {keyEvent.Key}" : "";
        log.LogDebug("Key: {Type}{Label} mods={Modifiers}", keyEvent.Type, label, keyEvent.Modifiers);

        using var s = AsyncHelper.RunSync(() => _state.WaitForDisposable());
        var st = s.Value;

        // consume both KeyDown and KeyUp for hotkeys so the slave never sees either half
        var hotkeyConsumed = (keyEvent.Modifiers & LockHotkey) == LockHotkey && keyEvent.Character is 'l' or 'm';
        if (hotkeyConsumed && keyEvent.Type == KeyEventType.KeyDown)
        {
            if (keyEvent.Character == 'l')
            {
                st.LockedToScreen = !st.LockedToScreen;
                log.LogInformation("Screen lock: {State}", st.LockedToScreen ? "locked" : "unlocked");
            }
            else if (st.Mouse.IsOnVirtualScreen && st.Mouse.CurrentScreen != null)
            {
                var screenName = st.Mouse.CurrentScreen.Name;
                var isNowRelative = !st.RelativeMouseScreens.GetValueOrDefault(screenName);
                st.RelativeMouseScreens[screenName] = isNowRelative;
                log.LogInformation("Mouse mode for '{Screen}': {Mode}", screenName, isNowRelative ? "relative" : "absolute");
            }
        }

        if (!hotkeyConsumed && st.Mouse.IsOnVirtualScreen && relay.IsConnected)
        {
            // include repeat settings on the first KeyDown for a key so the slave can generate local repeats
            int? repeatDelay = null, repeatRate = null;
            if (keyEvent.Type == KeyEventType.KeyDown)
            {
                repeatDelay = _repeatDelayMs;
                repeatRate = _repeatRateMs;
            }
            ForwardToVirtualScreen(st, MessageKind.KeyEvent, new KeyEventMessage(keyEvent.Type, keyEvent.Modifiers, keyEvent.Character, keyEvent.Key, repeatDelay, repeatRate));
        }
    }

    private void OnMouseButton(MouseButtonEvent e)
    {
        using var s = AsyncHelper.RunSync(() => _state.WaitForDisposable());
        var st = s.Value;
        if (st.Mouse.IsOnVirtualScreen && relay.IsConnected)
        {
            log.LogDebug("Mouse: {Type} {Button}", e.IsPressed ? "down" : "up", e.Button);
            ForwardToVirtualScreen(st, MessageKind.MouseButton, new MouseButtonMessage(e.Button, e.IsPressed));
        }
    }

    private void OnMouseScroll(MouseScrollEvent e)
    {
        using var s = AsyncHelper.RunSync(() => _state.WaitForDisposable());
        var st = s.Value;
        if (st.Mouse.IsOnVirtualScreen && relay.IsConnected)
        {
            log.LogDebug("Scroll: x={X} y={Y}", e.XDelta, e.YDelta);
            ForwardToVirtualScreen(st, MessageKind.MouseScroll, new MouseScrollMessage(e.XDelta, e.YDelta));
        }
    }

    private void SendMousePosition(LocalMasterState st, long now)
    {
        if (!relay.IsConnected || st.Mouse.CurrentScreen == null) return;

        var screen = st.Mouse.CurrentScreen;
        byte[] payload;

        if (st.RelativeMouseScreens.GetValueOrDefault(screen.Name))
        {
            // relative mode: send accumulated delta, preserve sub-pixel remainders
            var intDX = (int)st.PendingDX;
            var intDY = (int)st.PendingDY;
            if (intDX == 0 && intDY == 0) return;
            st.PendingDX -= intDX;
            st.PendingDY -= intDY;
            payload = MessageSerializer.Encode(MessageKind.MouseMoveDelta, new MouseMoveDeltaMessage(intDX, intDY));
        }
        else
        {
            // absolute mode: send current virtual position, discard accumulated deltas
            st.PendingDX = 0;
            st.PendingDY = 0;
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
        if (isRelative && st.PendingDX == 0 && st.PendingDY == 0) return;
        SendMousePosition(st, Environment.TickCount64);
    }

    private void ForwardToVirtualScreen<T>(LocalMasterState st, MessageKind kind, T message)
    {
        var target = st.Mouse.CurrentScreen?.Host;
        if (target == null) return;
        var payload = MessageSerializer.Encode(kind, message);
        _ = relay.Send([target], payload).AsTask();
    }

    private void OnMouseMove(double x, double y)
    {
        using var s = AsyncHelper.RunSync(() => _state.WaitForDisposable());
        var st = s.Value;
        if (st.Layout is null || st.ActiveLocalScreen is null) return;
        if (!st.Mouse.IsOnVirtualScreen)
            HandleRealScreenMove(st, x, y);
        else
            HandleVirtualScreenMove(st, x, y);
    }

    private void HandleRealScreenMove(LocalMasterState st, double x, double y)
    {
        if (st.PendingCursorShow)
        {
            st.PendingCursorShow = false;
            platform.ShowCursor();
        }

        // track which local screen the cursor is on
        var screen = FindLocalScreenAt(st, (int)x, (int)y) ?? st.ActiveLocalScreen!;
        if (screen != st.ActiveLocalScreen)
        {
            st.ActiveLocalScreen = screen;
            UpdateWarpPoint(st, screen);
        }

        if (st.LockedToScreen) return;

        // convert global cursor coords to screen-local
        var localX = (int)x - screen.X;
        var localY = (int)y - screen.Y;
        var hit = st.Layout!.DetectEdgeExit(screen, localX, localY);
        if (hit is null) return;
        if (!relay.IsConnected) return;

        var peerScreens = AsyncHelper.RunSync(() => _peerState.GetPeerScreensSnapshot().AsTask());
        var scale = GetRemoteScale(peerScreens, hit.Destination);
        var remoteInfo = GetRemoteScreensAndScales(st.Screens, peerScreens, hit.Destination);

        platform.HideCursor();
        platform.IsOnVirtualScreen = true;
        st.Mouse.EnterScreen(hit.Destination, remoteInfo.Screens, hit.EntryX, hit.EntryY, scale, remoteInfo.ScaleMap);
        st.PendingDX = 0;
        st.PendingDY = 0;
        st.LastWarpX = x;
        st.LastWarpY = y;
        log.LogInformation("Entered remote screen '{Name}' → ({X}, {Y})", hit.Destination.Name, hit.EntryX, hit.EntryY);

        if (relay.IsConnected)
        {
            var payload = MessageSerializer.Encode(MessageKind.EnterScreen, new EnterScreenMessage(hit.Destination.Name, hit.EntryX, hit.EntryY, hit.Destination.Width, hit.Destination.Height));
            _ = relay.Send([hit.Destination.Host], payload).AsTask();
        }
    }

    private void HandleVirtualScreenMove(LocalMasterState st, double x, double y)
    {
        var dx = x - st.LastWarpX;
        var dy = y - st.LastWarpY;

        // update before warp
        st.LastWarpX = x;
        st.LastWarpY = y;

        // zero-delta filter
        if (dx == 0 && dy == 0) return;

        // bogus filter: drop delta that looks like a warp-displacement artifact
        if (Math.Abs(dx) > st.HalfW - 10 || Math.Abs(dy) > st.HalfH - 10) return;

        var prevScreen = st.Mouse.ApplyDelta(dx, dy);
        if (prevScreen != null)
        {
            // intra-host screen transition: reset accumulators and send EnterScreen for new screen
            st.PendingDX = 0;
            st.PendingDY = 0;
            if (relay.IsConnected && st.Mouse.CurrentScreen != null)
            {
                var s = st.Mouse.CurrentScreen;
                var enterPayload = MessageSerializer.Encode(MessageKind.EnterScreen,
                    new EnterScreenMessage(s.Name, (int)st.Mouse.X, (int)st.Mouse.Y, s.Width, s.Height));
                _ = relay.Send([s.Host], enterPayload).AsTask();
            }
        }
        else
        {
            // same screen — accumulate scaled deltas for throttle
            st.PendingDX += dx * (double)st.Mouse.Scale;
            st.PendingDY += dy * (double)st.Mouse.Scale;
        }

        var now = Environment.TickCount64;
        if (now - st.LastVirtualLogTick >= 100)
        {
            st.LastVirtualLogTick = now;
            if (st.Mouse.CurrentScreen != null && st.RelativeMouseScreens.GetValueOrDefault(st.Mouse.CurrentScreen.Name))
                log.LogDebug("Mouse: ({X}, {Y})  Offset: ({DX}, {DY})", (int)st.Mouse.X, (int)st.Mouse.Y, (int)st.PendingDX, (int)st.PendingDY);
            else
                log.LogDebug("Mouse: ({X}, {Y})", (int)st.Mouse.X, (int)st.Mouse.Y);
        }

        // check if we've crossed back to a local screen
        var virtualScreen = st.Mouse.CurrentScreen!;
        var hit = st.Layout!.DetectEdgeExit(virtualScreen, (int)st.Mouse.X, (int)st.Mouse.Y);
        if (hit is not null && hit.Destination.IsLocal && !st.LockedToScreen)
        {
            var targetScreen = hit.Destination;

            // flush any pending delta before leaving
            FlushMouseDelta(st);

            // warp to entry in global OS coords; show is deferred to next real-screen event
            var globalX = targetScreen.X + hit.EntryX;
            var globalY = targetScreen.Y + hit.EntryY;
            var leavingScreen = st.Mouse.CurrentScreen;
            st.Mouse.LeaveScreen();
            ReturnToLocalScreen(globalX, globalY);
            st.PendingCursorShow = true;
            st.ActiveLocalScreen = targetScreen;
            UpdateWarpPoint(st, targetScreen);
            log.LogInformation("Returned to local screen ← ({X}, {Y})", globalX, globalY);

            if (relay.IsConnected && leavingScreen != null)
            {
                var payload = MessageSerializer.Encode(MessageKind.LeaveScreen, new { });
                _ = relay.Send([leavingScreen.Host], payload).AsTask();
            }
            return;
        }

        // throttle mouse sends to MaxMouseHz
        if (now - st.LastMouseSendTick >= MinMouseIntervalMs)
            SendMousePosition(st, now);

        // warp to center on every event
        platform.WarpCursor(st.WarpX, st.WarpY);
        st.LastWarpX = st.WarpX;
        st.LastWarpY = st.WarpY;
    }

    private void ReturnToLocalScreen(int x, int y)
    {
        platform.IsOnVirtualScreen = false;
        platform.WarpCursor(x, y);
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

    private static readonly JsonSerializerOptions _jsonOptions = new()
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    private void LogDetectedScreens(List<ScreenRect> screens)
    {
        log.LogInformation("Detected {Count} local screen(s):", screens.Count);
        for (var i = 0; i < screens.Count; i++)
            if (screens[i].Identity != null)
                log.LogInformation("  Screen {I}: {Json}", i, JsonSerializer.Serialize(screens[i].Identity, _jsonOptions));
    }

    // combines local screens with placeholder remote screens (Width=0 until ScreenInfo arrives)
    private static List<ScreenRect> BuildAllScreens(List<ScreenRect> localScreens, HydraConfig config)
    {
        var result = new List<ScreenRect>(localScreens);
        foreach (var host in config.RemoteHosts)
            result.Add(new ScreenRect(host.Name, host.Name, 0, 0, 0, 0, IsLocal: false));
        return result;
    }

    private class LocalMasterState
    {
        public List<ScreenRect> Screens = [];
        public List<ScreenRect> LocalScreens = [];
        public ScreenRect? ActiveLocalScreen;
        public ScreenLayout? Layout;
        public VirtualMouseState Mouse = new();
        public int WarpX, WarpY, HalfW, HalfH;
        public double LastWarpX, LastWarpY;
        public long LastVirtualLogTick;
        public bool PendingCursorShow;
        public bool LockedToScreen;

        // per-screen relative mouse mode (true = relative, false/absent = absolute)
        public Dictionary<string, bool> RelativeMouseScreens = new(StringComparer.OrdinalIgnoreCase);

        // screensaver sync: saved cursor location before screensaver snap-back
        public bool ScreensaverActive;
        public string? SavedScreenName;
        public int SavedCursorX, SavedCursorY;

        // 120Hz throttle: accumulated deltas and last send time
        public long LastMouseSendTick;
        public double PendingDX;
        public double PendingDY;
    }

    private record RemoteScreenInfo(List<ScreenRect> Screens, Dictionary<string, decimal> ScaleMap);
}

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
    ILoggerFactory loggerFactory,
    ILogger<ScreenTransitionService> log,
    IWorldState? peerState = null)
    : IHostedService
{
    private const KeyModifiers LockHotkey = KeyModifiers.Control | KeyModifiers.Alt | KeyModifiers.Super;
    private const int MaxMouseHz = 120;
    private static readonly double MinMouseIntervalMs = 1000.0 / MaxMouseHz;

    // cached key repeat settings from OS; refreshed periodically in the poll loop
    private (int DelayMs, int RateMs) _repeatSettings = (500, 33);
    private long _lastRepeatSettingsTick;

    private readonly IWorldState _peerState = peerState ?? new WorldState();
    private readonly SemaphoreSlimValue<LocalMasterState> _state = new(new LocalMasterState(), disposeValue: false);
    private CancellationTokenSource? _pollCts;

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

        var detected = platform.GetAllScreens();
        LogDetectedScreens(detected);

        using (var s = await _state.WaitForDisposable(cancellationToken))
        {
            var st = s.Value;
            st.LocalScreens = BuildLocalScreens(detected);
            st.ActiveLocalScreen = st.LocalScreens.FirstOrDefault();

            if (st.ActiveLocalScreen == null)
            {
                log.LogError("No local screens detected.");
                return;
            }

            UpdateWarpPoint(st, st.ActiveLocalScreen);
            st.Screens = BuildAllScreens(st.LocalScreens, config);
            st.Layout = new ScreenLayout(st.Screens, config.Hosts);

            foreach (var remote in st.Screens.Where(r => !r.IsLocal))
                log.LogInformation("Remote screen '{Name}': waiting for peer", remote.Name);
        }

        _repeatSettings = platform.GetKeyRepeatSettings();
        _lastRepeatSettingsTick = Environment.TickCount64;

        relay.PeersChanged += OnPeersChanged;
        relay.MessageReceived += OnMessageReceived;

        platform.StartEventTap((x, y) => OnMouseMove(x, y), OnKeyEvent, OnMouseButton, OnMouseScroll);

        _pollCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        _ = PollScreenChangesAsync(_pollCts.Token);
    }

    public async Task StopAsync(CancellationToken cancellationToken)
    {
        _pollCts?.Cancel();
        _pollCts?.Dispose();
        relay.PeersChanged -= OnPeersChanged;
        relay.MessageReceived -= OnMessageReceived;

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

    private async Task PollScreenChangesAsync(CancellationToken ct)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(2));
        try
        {
            while (await timer.WaitForNextTickAsync(ct))
            {
                var detected = platform.GetAllScreens();

                List<ScreenRect> localScreensSnapshot;
                using (var s = await _state.WaitForDisposable(ct))
                    localScreensSnapshot = s.Value.LocalScreens;

                // refresh key repeat settings every 30 seconds
                var now = Environment.TickCount64;
                if (now - _lastRepeatSettingsTick >= 30_000)
                {
                    _repeatSettings = platform.GetKeyRepeatSettings();
                    _lastRepeatSettingsTick = now;
                }

                if (!ScreenRect.ScreenListChanged(detected, localScreensSnapshot))
                    continue;

                log.LogInformation("Screen configuration changed — rebuilding layout");
                LogDetectedScreens(detected);
                var newLocal = BuildLocalScreens(detected);
                var newScreens = BuildAllScreens(newLocal, config);
                var peerScreens = await _peerState.GetPeerScreensSnapshot();

                using (var s = await _state.WaitForDisposable(ct))
                {
                    var st = s.Value;
                    ApplyPeerScreenSizes(peerScreens, newScreens);
                    var newLayout = new ScreenLayout(newScreens, config.Hosts);
                    st.LocalScreens = newLocal;
                    st.Screens = newScreens;
                    st.Layout = newLayout;

                    if (!st.Mouse.IsOnVirtualScreen)
                    {
                        st.ActiveLocalScreen = st.LocalScreens.FirstOrDefault() ?? st.ActiveLocalScreen;
                        if (st.ActiveLocalScreen != null) UpdateWarpPoint(st, st.ActiveLocalScreen);
                    }
                }
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
        var newLayout = new ScreenLayout(newScreens, config.Hosts);
        st.Screens = newScreens;
        st.Layout = newLayout;
        st.ActiveLocalScreen = st.LocalScreens.FirstOrDefault(s => s.Name.EqualsIgnoreCase(st.ActiveLocalScreen.Name)) ?? st.LocalScreens.FirstOrDefault() ?? st.ActiveLocalScreen;

        // if the cursor is on a remote screen whose dims changed, update it
        if (st.Mouse.IsOnVirtualScreen && st.Mouse.CurrentScreen != null)
        {
            var refreshed = st.Screens.FirstOrDefault(s => s.Name.EqualsIgnoreCase(st.Mouse.CurrentScreen.Name));
            if (refreshed != null && refreshed != st.Mouse.CurrentScreen)
            {
                var (remoteScreens, scaleMap) = GetRemoteScreensAndScales(st.Screens, peerScreens, refreshed);
                st.Mouse.EnterScreen(refreshed, remoteScreens, (int)st.Mouse.X, (int)st.Mouse.Y, GetRemoteScale(peerScreens, refreshed), scaleMap);
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
    private static (List<ScreenRect> screens, Dictionary<string, decimal> scaleMap) GetRemoteScreensAndScales(
        List<ScreenRect> allScreens, Dictionary<string, List<ScreenInfoEntry>> peerScreens, ScreenRect target)
    {
        var screens = allScreens.Where(s => !s.IsLocal && s.Host.EqualsIgnoreCase(target.Host)).ToList();
        var scaleMap = screens.ToDictionary(s => s.Name, s => GetRemoteScale(peerScreens, s), StringComparer.OrdinalIgnoreCase);
        return (screens, scaleMap);
    }

    private void OnKeyEvent(KeyEvent keyEvent)
    {
        var label = keyEvent.Character.HasValue ? $" '{keyEvent.Character}'" : keyEvent.Key.HasValue ? $" {keyEvent.Key}" : "";
        log.LogDebug("Key: {Type}{Label} mods={Modifiers}", keyEvent.Type, label, keyEvent.Modifiers);

        using var s = AsyncHelper.RunSync(() => _state.WaitForDisposable());
        var st = s.Value;

        if (keyEvent.Type == KeyEventType.KeyDown && (keyEvent.Modifiers & LockHotkey) == LockHotkey)
        {
            if (keyEvent.Character == 'l')
            {
                st.LockedToScreen = !st.LockedToScreen;
                log.LogInformation("Screen lock: {State}", st.LockedToScreen ? "locked" : "unlocked");
            }
            else if (keyEvent.Character == 'm' && st.Mouse.IsOnVirtualScreen && st.Mouse.CurrentScreen != null)
            {
                var screenName = st.Mouse.CurrentScreen.Name;
                var isNowRelative = !st.RelativeMouseScreens.GetValueOrDefault(screenName);
                st.RelativeMouseScreens[screenName] = isNowRelative;
                log.LogInformation("Mouse mode for '{Screen}': {Mode}", screenName, isNowRelative ? "relative" : "absolute");
            }
        }

        if (st.Mouse.IsOnVirtualScreen && relay.IsConnected)
        {
            // include repeat settings on the first KeyDown for a key so the slave can generate local repeats
            int? repeatDelay = null, repeatRate = null;
            if (keyEvent.Type == KeyEventType.KeyDown)
            {
                repeatDelay = _repeatSettings.DelayMs;
                repeatRate = _repeatSettings.RateMs;
            }
            ForwardToVirtualScreen(st, MessageKind.KeyEvent, new KeyEventMessage(keyEvent.Type, keyEvent.Modifiers, keyEvent.Character, keyEvent.Key, repeatDelay, repeatRate));
        }
    }

    private void OnMouseButton(MouseButtonEvent e)
    {
        log.LogDebug("Mouse: {Type} {Button}", e.IsPressed ? "down" : "up", e.Button);

        using var s = AsyncHelper.RunSync(() => _state.WaitForDisposable());
        var st = s.Value;
        if (st.Mouse.IsOnVirtualScreen && relay.IsConnected)
            ForwardToVirtualScreen(st, MessageKind.MouseButton, new MouseButtonMessage(e.Button, e.IsPressed));
    }

    private void OnMouseScroll(MouseScrollEvent e)
    {
        log.LogDebug("Scroll: x={X} y={Y}", e.XDelta, e.YDelta);

        using var s = AsyncHelper.RunSync(() => _state.WaitForDisposable());
        var st = s.Value;
        if (st.Mouse.IsOnVirtualScreen && relay.IsConnected)
            ForwardToVirtualScreen(st, MessageKind.MouseScroll, new MouseScrollMessage(e.XDelta, e.YDelta));
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

    // flush pending delta immediately (e.g. before leaving a virtual screen)
    private void FlushMouseDelta(LocalMasterState st)
    {
        if (!relay.IsConnected || st.Mouse.CurrentScreen == null) return;
        var hasPending = st.PendingDX != 0 || st.PendingDY != 0;
        if (!hasPending) return;
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

        var peerScreens = AsyncHelper.RunSync(() => _peerState.GetPeerScreensSnapshot().AsTask());
        var scale = GetRemoteScale(peerScreens, hit.Destination);
        var (remoteScreens, scaleMap) = GetRemoteScreensAndScales(st.Screens, peerScreens, hit.Destination);

        platform.HideCursor();
        platform.IsOnVirtualScreen = true;
        st.Mouse.EnterScreen(hit.Destination, remoteScreens, hit.EntryX, hit.EntryY, scale, scaleMap);
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

        // accumulate deltas for throttle
        st.PendingDX += dx;
        st.PendingDY += dy;

        st.Mouse.ApplyDelta(dx, dy);

        var now = Environment.TickCount64;
        if (now - st.LastVirtualLogTick >= 100)
        {
            st.LastVirtualLogTick = now;
            log.LogDebug("Virtual: ({X}, {Y})", (int)st.Mouse.X, (int)st.Mouse.Y);
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

    private void LogDetectedScreens(List<DetectedScreen> detected)
    {
        log.LogInformation("Detected {Count} local screen(s):", detected.Count);
        for (var i = 0; i < detected.Count; i++)
        {
            var d = detected[i];
            log.LogInformation("  Screen {I}: {W}x{H} @ ({X},{Y}) output={Output} name={Name}",
                i, d.Width, d.Height, d.X, d.Y, d.OutputName ?? "?", d.DisplayName ?? "?");
        }
    }

    // converts OS-detected screens to ScreenRects, naming single-screen = hostname, multi = hostname:N
    private List<ScreenRect> BuildLocalScreens(List<DetectedScreen> detected)
    {
        var result = new List<ScreenRect>();
        for (var i = 0; i < detected.Count; i++)
        {
            var d = detected[i];
            var name = ScreenNaming.BuildScreenName(config.ResolvedName, i, detected.Count);
            result.Add(new ScreenRect(name, config.ResolvedName, d.X, d.Y, d.Width, d.Height, IsLocal: true));
        }
        return result;
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

        // 120Hz throttle: accumulated deltas and last send time
        public long LastMouseSendTick;
        public double PendingDX;
        public double PendingDY;
    }
}

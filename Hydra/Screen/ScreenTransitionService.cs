using Cathedral.Extensions;
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
    ILogger<ScreenTransitionService> log)
    : IHostedService
{
    private List<ScreenRect> _screens = [];          // all screens: local + remote
    private List<ScreenRect> _localScreens = [];     // local OS-detected screens only
    private ScreenRect? _activeLocalScreen;           // which local screen the cursor is currently on
    private ScreenLayout? _layout;
    private readonly VirtualMouseState _mouse = new();

    // center of active local screen in global OS coords — cursor warped here on virtual events
    private int _warpX;
    private int _warpY;

    // half-dimensions of active local screen — used for bogus-delta filter
    private int _halfW;
    private int _halfH;

    // guards layout/screens shared between event-tap, relay, and poll threads
    private readonly Lock _lock = new();

    // last known cursor position; deltas computed from this
    private double _lastWarpX;
    private double _lastWarpY;

    // throttle virtual position logging to 10/sec
    private long _lastVirtualLogTick;

    // show cursor on next real-screen event (deferred to avoid flash at warp position)
    private bool _pendingCursorShow;

    // locked to current screen — toggled by Ctrl+Alt+Super+L
    private bool _lockedToScreen;
    private const KeyModifiers LockHotkey = KeyModifiers.Control | KeyModifiers.Alt | KeyModifiers.Super;

    // master-side peer tracking
    private readonly HashSet<string> _knownPeers = [];
    private readonly Dictionary<string, (int Width, int Height)> _peerScreens = [];
    private readonly Dictionary<string, ILogger> _slaveLoggers = [];

    private CancellationTokenSource? _pollCts;

    public Task StartAsync(CancellationToken cancellationToken)
    {
        if (!platform.IsAccessibilityTrusted())
        {
            log.LogError("Input hook permission not granted. On macOS: grant access in System Settings > Privacy & Security > Accessibility. Then restart Hydra.");
            return Task.CompletedTask;
        }

        log.LogInformation("Host: {Name}", config.ResolvedName);

        if (config.LocalHost == null && config.Hosts.Count > 0)
        {
            log.LogError("Host '{Name}' is not listed in the config hosts — add it to the hosts list.", config.ResolvedName);
            return Task.CompletedTask;
        }

        var detected = platform.GetAllScreens();
        LogDetectedScreens(detected);
        _localScreens = BuildLocalScreens(detected);
        _activeLocalScreen = _localScreens.FirstOrDefault();

        if (_activeLocalScreen == null)
        {
            log.LogError("No local screens detected.");
            return Task.CompletedTask;
        }

        UpdateWarpPoint(_activeLocalScreen);
        _screens = BuildAllScreens(_localScreens, config);
        _layout = new ScreenLayout(_screens, config.Hosts);

        foreach (var remote in _screens.Where(s => !s.IsLocal))
            log.LogInformation("Remote screen '{Name}': waiting for peer", remote.Name);

        relay.PeersChanged += OnPeersChanged;
        relay.MessageReceived += OnMessageReceived;

        platform.StartEventTap((x, y) => OnMouseMove(x, y), OnKeyEvent, OnMouseButton, OnMouseScroll);

        // start background polling for screen configuration changes
        _pollCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        _ = PollScreenChangesAsync(_pollCts.Token);

        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken)
    {
        _pollCts?.Cancel();
        _pollCts?.Dispose();
        relay.PeersChanged -= OnPeersChanged;
        relay.MessageReceived -= OnMessageReceived;

        platform.StopEventTap();
        if (_mouse.IsOnVirtualScreen || _pendingCursorShow)
        {
            _pendingCursorShow = false;
            platform.IsOnVirtualScreen = false;
            platform.ShowCursor();
        }
        return Task.CompletedTask;
    }

    private async Task PollScreenChangesAsync(CancellationToken ct)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(2));
        try
        {
            while (await timer.WaitForNextTickAsync(ct))
            {
                var detected = platform.GetAllScreens();
                if (!ScreenListChanged(detected, _localScreens))
                    continue;

                log.LogInformation("Screen configuration changed — rebuilding layout");
                LogDetectedScreens(detected);
                var newLocal = BuildLocalScreens(detected);
                var newScreens = BuildAllScreens(newLocal, config);
                ApplyPeerScreenSizes(newScreens);
                var newLayout = new ScreenLayout(newScreens, config.Hosts);

                lock (_lock)
                {
                    _localScreens = newLocal;
                    _screens = newScreens;
                    _layout = newLayout;

                    // if cursor is on local screen, snap active screen to match new layout
                    if (!_mouse.IsOnVirtualScreen)
                    {
                        _activeLocalScreen = _localScreens.FirstOrDefault() ?? _activeLocalScreen;
                        if (_activeLocalScreen != null) UpdateWarpPoint(_activeLocalScreen);
                    }
                }
            }
        }
        catch (OperationCanceledException) { }
    }

    private static bool ScreenListChanged(List<DetectedScreen> detected, List<ScreenRect> current)
    {
        if (detected.Count != current.Count) return true;
        for (var i = 0; i < detected.Count; i++)
        {
            var d = detected[i];
            var s = current[i];
            if (d.X != s.X || d.Y != s.Y || d.Width != s.Width || d.Height != s.Height)
                return true;
        }
        return false;
    }

    private void OnPeersChanged(string[] hostNames)
    {
        var current = new HashSet<string>(hostNames);
        var configuredSlaves = config.RemoteHosts
            .Select(s => s.Name)
            .ToHashSet();

        // snap back if the peer we're currently on has disconnected
        if (_mouse.IsOnVirtualScreen && _mouse.CurrentScreen != null)
        {
            var currentName = _mouse.CurrentScreen.Name;
            if (!current.Contains(currentName))
            {
                _mouse.LeaveScreen();
                platform.IsOnVirtualScreen = false;
                platform.WarpCursor(_warpX, _warpY);
                platform.ShowCursor();
                _pendingCursorShow = false;
                log.LogInformation("Remote peer '{Name}' disconnected — returned to local screen", currentName);
            }
        }

        // send MasterConfig only to newly appeared peers that are configured as slaves
        foreach (var host in current.Where(h => !_knownPeers.Contains(h) && configuredSlaves.Contains(h)))
        {
            var payload = MessageSerializer.Encode(MessageKind.MasterConfig, new { });
            _ = relay.Send([host], payload).AsTask();
            log.LogDebug("Sent MasterConfig to {Host}", host);
        }

        // remove departed peers so they re-trigger on next join
        var anyDeparted = false;
        foreach (var departed in _knownPeers.Where(h => !current.Contains(h)).ToList())
        {
            _knownPeers.Remove(departed);
            _peerScreens.Remove(departed);
            anyDeparted = true;
        }

        // rebuild layout so departed screens go back to Width=0 (offline) and can't be transitioned to
        if (anyDeparted)
            UpdateVirtualScreenDimensions();

        _knownPeers.UnionWith(current);
    }

    private void OnMessageReceived(string sourceHost, MessageKind kind, string json)
    {
        switch (kind)
        {
            case MessageKind.ScreenInfo:
                var info = json.FromSaneJson<ScreenInfoMessage>();
                if (info != null && info.Screens.Count > 0)
                {
                    // use primary screen dims for now (full multi-screen map in slave Phase 9)
                    var primary = info.Screens[0];
                    _peerScreens[sourceHost] = (primary.Width, primary.Height);
                    UpdateVirtualScreenDimensions();
                    log.LogInformation("Screen info from {Host}: {Count} screen(s), primary {W}x{H}", sourceHost, info.Screens.Count, primary.Width, primary.Height);
                }
                break;
            case MessageKind.SlaveLog:
                var entry = json.FromSaneJson<SlaveLogMessage>();
                if (entry != null) ForwardSlaveLog(sourceHost, entry);
                break;
        }
    }

    private void ForwardSlaveLog(string sourceHost, SlaveLogMessage entry)
    {
        var category = $"slave:{sourceHost}/{entry.Category}";
        if (!_slaveLoggers.TryGetValue(category, out var logger))
        {
            logger = loggerFactory.CreateLogger(category);
            _slaveLoggers[category] = logger;
        }

        var level = (LogLevel)entry.Level;
        // ReSharper disable TemplateIsNotCompileTimeConstantProblem
        if (entry.Exception != null)
            logger.Log(level, "{Message}\n{Exception}", entry.Message, entry.Exception);
        else
            logger.Log(level, "{Message}", entry.Message);
        // ReSharper restore TemplateIsNotCompileTimeConstantProblem
    }

    private void UpdateVirtualScreenDimensions()
    {
        if (_activeLocalScreen == null) return;

        var newScreens = BuildAllScreens(_localScreens, config);
        ApplyPeerScreenSizes(newScreens);
        var newLayout = new ScreenLayout(newScreens, config.Hosts);

        lock (_lock)
        {
            _screens = newScreens;
            _layout = newLayout;
            _activeLocalScreen = _localScreens.FirstOrDefault(s => s.Name == _activeLocalScreen.Name) ?? _localScreens.FirstOrDefault() ?? _activeLocalScreen;

            // if the cursor is on a remote screen whose dims changed, update it
            if (_mouse.IsOnVirtualScreen && _mouse.CurrentScreen != null)
            {
                var refreshed = _screens.FirstOrDefault(s => s.Name == _mouse.CurrentScreen.Name);
                if (refreshed != null && refreshed != _mouse.CurrentScreen)
                    _mouse.EnterScreen(refreshed, _mouse.RemoteScreens, (int)_mouse.X, (int)_mouse.Y, _mouse.Scale);
            }
        }

        log.LogDebug("Screen layout updated");
    }

    private void ApplyPeerScreenSizes(List<ScreenRect> screens)
    {
        for (var i = 0; i < screens.Count; i++)
        {
            var screen = screens[i];
            if (!screen.IsLocal && _peerScreens.TryGetValue(screen.Name, out var dims))
                screens[i] = screen with { Width = dims.Width, Height = dims.Height };
        }
    }

    private void OnKeyEvent(KeyEvent keyEvent)
    {
        var label = keyEvent.Character.HasValue ? $" '{keyEvent.Character}'" : keyEvent.Key.HasValue ? $" {keyEvent.Key}" : "";
        log.LogDebug("Key: {Type}{Label} mods={Modifiers}", keyEvent.Type, label, keyEvent.Modifiers);

        if (keyEvent.Type == KeyEventType.KeyDown
            && keyEvent.Character == 'l'
            && (keyEvent.Modifiers & LockHotkey) == LockHotkey)
        {
            _lockedToScreen = !_lockedToScreen;
            log.LogInformation("Screen lock: {State}", _lockedToScreen ? "locked" : "unlocked");
        }

        if (_mouse.IsOnVirtualScreen && relay.IsConnected)
            ForwardToVirtualScreen(MessageKind.KeyEvent, new KeyEventMessage(keyEvent.Type, keyEvent.Modifiers, keyEvent.Character, keyEvent.Key));
    }

    private void OnMouseButton(MouseButtonEvent e)
    {
        log.LogDebug("Mouse: {Type} {Button}", e.IsPressed ? "down" : "up", e.Button);

        if (_mouse.IsOnVirtualScreen && relay.IsConnected)
            ForwardToVirtualScreen(MessageKind.MouseButton, new MouseButtonMessage(e.Button, e.IsPressed));
    }

    private void OnMouseScroll(MouseScrollEvent e)
    {
        log.LogDebug("Scroll: x={X} y={Y}", e.XDelta, e.YDelta);

        if (_mouse.IsOnVirtualScreen && relay.IsConnected)
            ForwardToVirtualScreen(MessageKind.MouseScroll, new MouseScrollMessage(e.XDelta, e.YDelta));
    }

    private void ForwardToVirtualScreen<T>(MessageKind kind, T message)
    {
        var target = _mouse.CurrentScreen?.Name;
        if (target == null) return;
        var payload = MessageSerializer.Encode(kind, message);
        _ = relay.Send([target], payload).AsTask();
    }

    private void OnMouseMove(double x, double y)
    {
        ScreenLayout? layout;
        ScreenRect? activeLocalScreen;
        lock (_lock)
        {
            layout = _layout;
            activeLocalScreen = _activeLocalScreen;
        }
        if (layout is null || activeLocalScreen is null) return;

        if (!_mouse.IsOnVirtualScreen)
            HandleRealScreenMove(x, y, layout, activeLocalScreen);
        else
            HandleVirtualScreenMove(x, y, layout);
    }

    private void HandleRealScreenMove(double x, double y, ScreenLayout layout, ScreenRect activeLocalScreen)
    {
        if (_pendingCursorShow)
        {
            _pendingCursorShow = false;
            platform.ShowCursor();
        }

        // track which local screen the cursor is on
        var screen = FindLocalScreenAt((int)x, (int)y) ?? activeLocalScreen;
        if (screen != activeLocalScreen)
        {
            lock (_lock)
            {
                _activeLocalScreen = screen;
                UpdateWarpPoint(screen);
            }
        }

        if (_lockedToScreen) return;

        // convert global cursor coords to screen-local
        var localX = (int)x - screen.X;
        var localY = (int)y - screen.Y;
        var hit = layout.DetectEdgeExit(screen, localX, localY);
        if (hit is null) return;

        platform.HideCursor();
        platform.IsOnVirtualScreen = true;
        _mouse.EnterScreen(hit.Destination, [], hit.EntryX, hit.EntryY, 1.0m);
        _lastWarpX = x;
        _lastWarpY = y;
        log.LogInformation("Entered remote screen '{Name}' → ({X}, {Y})", hit.Destination.Name, hit.EntryX, hit.EntryY);

        if (relay.IsConnected)
        {
            var payload = MessageSerializer.Encode(MessageKind.EnterScreen, new EnterScreenMessage(hit.Destination.Name, hit.EntryX, hit.EntryY, hit.Destination.Width, hit.Destination.Height));
            _ = relay.Send([hit.Destination.Name], payload).AsTask();
        }
    }

    private void HandleVirtualScreenMove(double x, double y, ScreenLayout layout)
    {
        var dx = x - _lastWarpX;
        var dy = y - _lastWarpY;

        // update before warp
        _lastWarpX = x;
        _lastWarpY = y;

        // zero-delta filter
        if (dx == 0 && dy == 0) return;

        // bogus filter: drop delta that looks like a warp-displacement artifact
        int halfW, halfH;
        lock (_lock) { halfW = _halfW; halfH = _halfH; }
        if (Math.Abs(dx) > halfW - 10 || Math.Abs(dy) > halfH - 10) return;

        _mouse.ApplyDelta(dx, dy);

        var now = Environment.TickCount64;
        if (now - _lastVirtualLogTick >= 100)
        {
            _lastVirtualLogTick = now;
            log.LogDebug("Virtual: ({X}, {Y})", (int)_mouse.X, (int)_mouse.Y);
        }

        // check if we've crossed back to a local screen
        var virtualScreen = _mouse.CurrentScreen!;
        var hit = layout.DetectEdgeExit(virtualScreen, (int)_mouse.X, (int)_mouse.Y);
        if (hit is not null && hit.Destination.IsLocal && !_lockedToScreen)
        {
            var targetScreen = hit.Destination;

            // warp to entry in global OS coords; show is deferred to next real-screen event
            var globalX = targetScreen.X + hit.EntryX;
            var globalY = targetScreen.Y + hit.EntryY;
            var leavingScreen = _mouse.CurrentScreen;
            _mouse.LeaveScreen();
            platform.IsOnVirtualScreen = false;
            platform.WarpCursor(globalX, globalY);
            _pendingCursorShow = true;
            lock (_lock)
            {
                _activeLocalScreen = targetScreen;
                UpdateWarpPoint(targetScreen);
            }
            log.LogInformation("Returned to local screen ← ({X}, {Y})", globalX, globalY);

            if (relay.IsConnected && leavingScreen != null)
            {
                var payload = MessageSerializer.Encode(MessageKind.LeaveScreen, new { });
                _ = relay.Send([leavingScreen.Name], payload).AsTask();
            }
            return;
        }

        // forward current virtual position to remote
        if (relay.IsConnected && _mouse.CurrentScreen != null)
        {
            var payload = MessageSerializer.Encode(MessageKind.MouseMove, new MouseMoveMessage(_mouse.CurrentScreen.Name, (int)_mouse.X, (int)_mouse.Y));
            _ = relay.Send([_mouse.CurrentScreen.Name], payload).AsTask();
        }

        // warp to center on every event
        int warpX, warpY;
        lock (_lock) { warpX = _warpX; warpY = _warpY; }
        platform.WarpCursor(warpX, warpY);
        _lastWarpX = warpX;
        _lastWarpY = warpY;
    }

    private void UpdateWarpPoint(ScreenRect screen)
    {
        _halfW = screen.Width / 2;
        _halfH = screen.Height / 2;
        _warpX = screen.X + _halfW;
        _warpY = screen.Y + _halfH;
    }

    private ScreenRect? FindLocalScreenAt(int x, int y) =>
        _localScreens.FirstOrDefault(s => x >= s.X && x < s.X + s.Width && y >= s.Y && y < s.Y + s.Height);

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
            var name = detected.Count == 1 ? config.ResolvedName : $"{config.ResolvedName}:{i}";
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
}

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
    private List<ScreenRect> _screens = [];
    private ScreenRect? _realScreen;
    private ScreenLayout? _layout;
    private readonly VirtualMouseState _mouse = new();

    // center of the real screen -- cursor warped here on every virtual-screen event
    private int _warpX;
    private int _warpY;

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

    // debug screens — IsVirtual in config, no real slave, skip relay sends
    private readonly HashSet<string> _debugScreens = config.Screens
        .Where(s => s.IsVirtual)
        .Select(s => s.Name)
        .ToHashSet(StringComparer.OrdinalIgnoreCase);

    public Task StartAsync(CancellationToken cancellationToken)
    {
        if (!platform.IsAccessibilityTrusted())
        {
            log.LogError("Input hook permission not granted. On macOS: grant access in System Settings > Privacy & Security > Accessibility. Then restart Hydra.");
            return Task.CompletedTask;
        }

        log.LogInformation("Host: {Name}", config.ResolvedName);

        var bounds = platform.GetPrimaryScreenBounds();
        _screens = BuildScreens(config, bounds);

        _realScreen = _screens.FirstOrDefault(s => !s.IsVirtual);
        if (_realScreen == null)
        {
            log.LogError("No local screen found in config — add a screen whose name matches this host ({Name}).", config.ResolvedName);
            return Task.CompletedTask;
        }

        _warpX = _realScreen.Width / 2;
        _warpY = _realScreen.Height / 2;
        _layout = new ScreenLayout(_screens, config.Screens);
        log.LogInformation("Local screen: {W}x{H}", _realScreen.Width, _realScreen.Height);

        foreach (var remote in _screens.Where(s => s.IsVirtual))
            log.LogInformation("Remote screen '{Name}': {W}x{H}", remote.Name, remote.Width, remote.Height);

        relay.PeersChanged += OnPeersChanged;
        relay.MessageReceived += OnMessageReceived;

        platform.StartEventTap((x, y) => OnMouseMove(x, y), OnKeyEvent, OnMouseButton, OnMouseScroll);
        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken)
    {
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

    private void OnPeersChanged(string[] hostNames)
    {
        var current = new HashSet<string>(hostNames);
        var configuredSlaves = config.RemoteScreens
            .Where(s => !s.IsVirtual)
            .Select(s => s.Name)
            .ToHashSet();

        // snap back if the peer we're currently on has disconnected
        if (_mouse.IsOnVirtualScreen && _mouse.CurrentScreen != null)
        {
            var currentName = _mouse.CurrentScreen.Name;
            if (!current.Contains(currentName) && !_debugScreens.Contains(currentName))
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
                if (info != null)
                {
                    _peerScreens[sourceHost] = (info.Width, info.Height);
                    UpdateVirtualScreenDimensions();
                    log.LogInformation("Screen info from {Host}: {W}x{H}", sourceHost, info.Width, info.Height);
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
        if (_realScreen == null) return;

        var bounds = platform.GetPrimaryScreenBounds();
        var updated = BuildScreens(config, bounds);

        // apply known peer screen sizes to matching remote screens
        for (var i = 0; i < updated.Count; i++)
        {
            var screen = updated[i];
            if (screen.IsVirtual && !_debugScreens.Contains(screen.Name) && _peerScreens.TryGetValue(screen.Name, out var dims))
                updated[i] = screen with { Width = dims.Width, Height = dims.Height };
        }

        _screens = updated;
        _realScreen = _screens.First(s => !s.IsVirtual);
        _layout = new ScreenLayout(_screens, config.Screens);

        // if the cursor is on a remote screen whose dims changed, update it
        if (_mouse.IsOnVirtualScreen && _mouse.CurrentScreen != null)
        {
            var refreshed = _screens.FirstOrDefault(s => s.Name == _mouse.CurrentScreen.Name);
            if (refreshed != null && refreshed != _mouse.CurrentScreen)
                _mouse.EnterScreen(refreshed, (int)_mouse.X, (int)_mouse.Y, _mouse.Scale);
        }

        log.LogDebug("Screen layout updated");
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

        if (_mouse.IsOnVirtualScreen && relay.IsConnected && !IsDebugScreen(_mouse.CurrentScreen))
            ForwardToVirtualScreen(MessageKind.KeyEvent, new KeyEventMessage(keyEvent.Type, keyEvent.Modifiers, keyEvent.Character, keyEvent.Key));
    }

    private void OnMouseButton(MouseButtonEvent e)
    {
        log.LogDebug("Mouse: {Type} {Button}", e.IsPressed ? "down" : "up", e.Button);

        if (_mouse.IsOnVirtualScreen && relay.IsConnected && !IsDebugScreen(_mouse.CurrentScreen))
            ForwardToVirtualScreen(MessageKind.MouseButton, new MouseButtonMessage(e.Button, e.IsPressed));
    }

    private void OnMouseScroll(MouseScrollEvent e)
    {
        log.LogDebug("Scroll: x={X} y={Y}", e.XDelta, e.YDelta);

        if (_mouse.IsOnVirtualScreen && relay.IsConnected && !IsDebugScreen(_mouse.CurrentScreen))
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
        if (_layout is null || _realScreen is null) return;

        if (!_mouse.IsOnVirtualScreen)
            HandleRealScreenMove(x, y);
        else
            HandleVirtualScreenMove(x, y);
    }

    private void HandleRealScreenMove(double x, double y)
    {
        if (_pendingCursorShow)
        {
            _pendingCursorShow = false;
            platform.ShowCursor();
        }

        if (_lockedToScreen) return;

        var ix = (int)x;
        var iy = (int)y;
        var hit = _layout!.DetectEdgeExit(_realScreen!, ix, iy);
        if (hit is null) return;

        platform.HideCursor();
        platform.IsOnVirtualScreen = true;
        _mouse.EnterScreen(hit.Destination, hit.EntryX, hit.EntryY, hit.Scale);
        _lastWarpX = x;
        _lastWarpY = y;
        log.LogInformation("Entered remote screen '{Name}' → ({X}, {Y})", hit.Destination.Name, hit.EntryX, hit.EntryY);

        if (relay.IsConnected && !IsDebugScreen(hit.Destination))
        {
            var payload = MessageSerializer.Encode(MessageKind.EnterScreen, new EnterScreenMessage(hit.EntryX, hit.EntryY, hit.Destination.Width, hit.Destination.Height));
            _ = relay.Send([hit.Destination.Name], payload).AsTask();
        }
    }

    private void HandleVirtualScreenMove(double x, double y)
    {
        var dx = x - _lastWarpX;
        var dy = y - _lastWarpY;

        // update before warp
        _lastWarpX = x;
        _lastWarpY = y;

        // zero-delta filter
        if (dx == 0 && dy == 0) return;

        // bogus filter: drop delta that looks like a warp-displacement artifact
        if (Math.Abs(dx) > _warpX - 10 || Math.Abs(dy) > _warpY - 10) return;

        _mouse.ApplyDelta(dx, dy);

        var now = Environment.TickCount64;
        if (now - _lastVirtualLogTick >= 100)
        {
            _lastVirtualLogTick = now;
            log.LogDebug("Virtual: ({X}, {Y})", (int)_mouse.X, (int)_mouse.Y);
        }

        // check if we've crossed back to the real screen
        var virtualScreen = _mouse.CurrentScreen!;
        var hit = _layout!.DetectEdgeExit(virtualScreen, (int)_mouse.X, (int)_mouse.Y);
        if (hit is not null && !hit.Destination.IsVirtual && !_lockedToScreen)
        {
            // warp to entry while still hidden; show is deferred to next real-screen event to avoid flash
            var leavingScreen = _mouse.CurrentScreen;
            _mouse.LeaveScreen();
            platform.IsOnVirtualScreen = false;
            platform.WarpCursor(hit.EntryX, hit.EntryY);
            _pendingCursorShow = true;
            log.LogInformation("Returned to local screen ← ({X}, {Y})", hit.EntryX, hit.EntryY);

            if (relay.IsConnected && leavingScreen != null && !IsDebugScreen(leavingScreen))
            {
                var payload = MessageSerializer.Encode(MessageKind.LeaveScreen, new { });
                _ = relay.Send([leavingScreen.Name], payload).AsTask();
            }
            return;
        }

        // forward current virtual position to remote
        if (relay.IsConnected && _mouse.CurrentScreen != null && !IsDebugScreen(_mouse.CurrentScreen))
        {
            var payload = MessageSerializer.Encode(MessageKind.MouseMove, new MouseMoveMessage((int)_mouse.X, (int)_mouse.Y));
            _ = relay.Send([_mouse.CurrentScreen.Name], payload).AsTask();
        }

        // warp to center on every event
        platform.WarpCursor(_warpX, _warpY);
        _lastWarpX = _warpX;
        _lastWarpY = _warpY;
    }

    // builds runtime screen rects from config.
    // local screen gets real dimensions from OS; remote screens start at 0x0 until ScreenInfo arrives;
    // debug (IsVirtual) screens get fixed 1920x1080.
    private static List<ScreenRect> BuildScreens(HydraConfig config, ScreenRect bounds)
    {
        var result = new List<ScreenRect>();
        foreach (var screen in config.Screens)
        {
            if (screen.Name == config.ResolvedName)
                result.Add(new ScreenRect(screen.Name, bounds.Width, bounds.Height));
            else if (screen.IsVirtual)
                result.Add(new ScreenRect(screen.Name, 1920, 1080, true));
            else
                result.Add(new ScreenRect(screen.Name, 0, 0, true));
        }
        return result;
    }

    private bool IsDebugScreen(ScreenRect? screen) =>
        screen != null && _debugScreens.Contains(screen.Name);
}

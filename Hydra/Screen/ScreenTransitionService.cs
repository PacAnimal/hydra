using System.Text.Json;
using Cathedral.Config;
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
    private ScreenLayout? _layout;
    private List<ScreenRect> _screens = [];
    private ScreenRect? _realScreen;
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

    public Task StartAsync(CancellationToken cancellationToken)
    {
        if (!platform.IsAccessibilityTrusted())
        {
            log.LogError("Input hook permission not granted. On macOS: grant access in System Settings > Privacy & Security > Accessibility. Then restart Hydra.");
            return Task.CompletedTask;
        }

        var bounds = platform.GetPrimaryScreenBounds();
        _screens = ResolveScreens(config, bounds);

        _realScreen = _screens.First(s => !s.IsVirtual);
        _warpX = _realScreen.X + _realScreen.Width / 2;
        _warpY = _realScreen.Y + _realScreen.Height / 2;
        _layout = new ScreenLayout(_screens);

        log.LogInformation("Real screen: {W}x{H} at ({X},{Y})", _realScreen.Width, _realScreen.Height, _realScreen.X, _realScreen.Y);

        var virtualScreen = _screens.First(s => s.IsVirtual);
        log.LogInformation("Virtual screen: {W}x{H} at ({X},{Y})", virtualScreen.Width, virtualScreen.Height, virtualScreen.X, virtualScreen.Y);

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
        var configuredSlaves = config.Screens.Where(s => s.IsVirtual).Select(s => s.Name).ToHashSet();

        // send MasterConfig only to newly appeared peers that are configured as slaves
        foreach (var host in current.Where(h => !_knownPeers.Contains(h) && configuredSlaves.Contains(h)))
        {
            var payload = MessageSerializer.Encode(MessageKind.MasterConfig, new { });
            _ = relay.Send([host], payload).AsTask();
            log.LogDebug("Sent MasterConfig to {Host}", host);
        }

        // remove departed peers so they re-trigger on next join
        foreach (var departed in _knownPeers.Where(h => !current.Contains(h)).ToList())
        {
            _knownPeers.Remove(departed);
            _peerScreens.Remove(departed);
        }

        _knownPeers.UnionWith(current);
    }

    private void OnMessageReceived(string sourceHost, MessageKind kind, string json)
    {
        switch (kind)
        {
            case MessageKind.ScreenInfo:
                var info = JsonSerializer.Deserialize<ScreenInfoMessage>(json, SaneJson.Options);
                if (info != null)
                {
                    _peerScreens[sourceHost] = (info.Width, info.Height);
                    UpdateVirtualScreenDimensions();
                    log.LogInformation("Screen info from {Host}: {W}x{H}", sourceHost, info.Width, info.Height);
                }
                break;
            case MessageKind.SlaveLog:
                var entry = JsonSerializer.Deserialize<SlaveLogMessage>(json, SaneJson.Options);
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
        var updated = ResolveScreens(config, bounds);

        // apply known peer screen sizes to matching virtual screens
        for (var i = 0; i < updated.Count; i++)
        {
            var screen = updated[i];
            if (screen.IsVirtual && _peerScreens.TryGetValue(screen.Name, out var dims))
                updated[i] = screen with { Width = dims.Width, Height = dims.Height };
        }

        _screens = updated;
        _realScreen = _screens.First(s => !s.IsVirtual);
        _layout = new ScreenLayout(_screens);

        // if the cursor is on a virtual screen whose dims changed, update it
        if (_mouse.IsOnVirtualScreen && _mouse.CurrentScreen != null)
        {
            var refreshed = _screens.FirstOrDefault(s => s.Name == _mouse.CurrentScreen.Name);
            if (refreshed != null && refreshed != _mouse.CurrentScreen)
                _mouse.EnterScreen(refreshed, (int)_mouse.X, (int)_mouse.Y);
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
        _mouse.EnterScreen(hit.Destination, hit.EntryX, hit.EntryY);
        _lastWarpX = x;
        _lastWarpY = y;
        log.LogInformation("Entered virtual screen → ({X}, {Y})", (int)_mouse.X, (int)_mouse.Y);

        if (relay.IsConnected)
        {
            var payload = MessageSerializer.Encode(MessageKind.EnterScreen, new EnterScreenMessage(hit.EntryX, hit.EntryY, hit.Destination.Width, hit.Destination.Height));
            _ = relay.Send([hit.Destination.Name], payload).AsTask();
        }
    }

    private void HandleVirtualScreenMove(double x, double y)
    {
        var dx = x - _lastWarpX;
        var dy = y - _lastWarpY;

        // update before warp (synergy: m_xCursor = mx before warpCursor)
        _lastWarpX = x;
        _lastWarpY = y;

        // zero-delta filter
        if (dx == 0 && dy == 0) return;

        // bogus filter: drop delta that looks like a warp-displacement artifact (synergy lines 1065-1071)
        var centerToEdgeX = Math.Abs(_warpX - _realScreen!.X);
        var centerToEdgeY = Math.Abs(_warpY - _realScreen!.Y);
        if (Math.Abs(dx) > centerToEdgeX - 10 || Math.Abs(dy) > centerToEdgeY - 10) return;

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
            log.LogInformation("Returned to real screen ← ({X}, {Y})", hit.EntryX, hit.EntryY);

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
            var payload = MessageSerializer.Encode(MessageKind.MouseMove, new MouseMoveMessage((int)_mouse.X, (int)_mouse.Y));
            _ = relay.Send([_mouse.CurrentScreen.Name], payload).AsTask();
        }

        // warp to center on every event (synergy's approach; suppression interval keeps acceleration intact)
        platform.WarpCursor(_warpX, _warpY);
        _lastWarpX = _warpX;
        _lastWarpY = _warpY;
    }

    // fills in screens with zero width/height using real display bounds,
    // and positions the virtual screen to the right of the real one.
    // default virtual screen size is 100x100 until peer reports its real dimensions.
    private static List<ScreenRect> ResolveScreens(HydraConfig config, ScreenRect primaryBounds)
    {
        var result = new List<ScreenRect>();
        ScreenRect? realScreen = null;

        foreach (var def in config.Screens)
        {
            var w = def.Width == 0 ? primaryBounds.Width : def.Width;
            var h = def.Height == 0 ? primaryBounds.Height : def.Height;
            var x = def.X;
            var y = def.Y;

            if (!def.IsVirtual)
            {
                realScreen = new ScreenRect(def.Name, x, y, w, h, false);
                result.Add(realScreen);
            }
            else
            {
                // position virtual screen to the right of the real one; default 100x100 until peer responds
                var rx = realScreen?.X + (realScreen?.Width ?? primaryBounds.Width);
                var vw = def.Width == 0 ? 100 : w;
                var vh = def.Height == 0 ? 100 : h;
                result.Add(new ScreenRect(def.Name, rx ?? x, y, vw, vh, true));
            }
        }

        return result;
    }
}

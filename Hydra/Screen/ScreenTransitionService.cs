using Hydra.Config;
using Hydra.Platform;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Hydra.Screen;

public class ScreenTransitionService(IPlatformInput platform, ILogger<ScreenTransitionService> log) : IHostedService
{
    private ScreenLayout? _layout;
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

    public Task StartAsync(CancellationToken cancellationToken)
    {
        if (!platform.IsAccessibilityTrusted())
        {
            log.LogError("Accessibility permission not granted. Grant access in System Settings > Privacy & Security > Accessibility, then restart Hydra.");
            return Task.CompletedTask;
        }

        var bounds = platform.GetPrimaryScreenBounds();
        var config = HydraConfig.Load();
        var screens = ResolveScreens(config, bounds);

        _realScreen = screens.First(s => !s.IsVirtual);
        _warpX = _realScreen.X + _realScreen.Width / 2;
        _warpY = _realScreen.Y + _realScreen.Height / 2;
        _layout = new ScreenLayout(screens);

        log.LogInformation("Real screen: {W}x{H} at ({X},{Y})", _realScreen.Width, _realScreen.Height, _realScreen.X, _realScreen.Y);

        var virtualScreen = screens.First(s => s.IsVirtual);
        log.LogInformation("Virtual screen: {W}x{H} at ({X},{Y})", virtualScreen.Width, virtualScreen.Height, virtualScreen.X, virtualScreen.Y);

        platform.StartEventTap((x, y) => OnMouseMove(x, y));
        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken)
    {
        platform.StopEventTap();
        if (_mouse.IsOnVirtualScreen)
        {
            platform.IsOnVirtualScreen = false;
            platform.ShowCursor();
        }
        return Task.CompletedTask;
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

        // warp to center on every event (synergy's approach; suppression interval keeps acceleration intact)
        platform.WarpCursor(_warpX, _warpY);
        _lastWarpX = _warpX;
        _lastWarpY = _warpY;

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
        if (hit is null || hit.Destination.IsVirtual) return;

        // returning to real screen
        _mouse.LeaveScreen();
        platform.IsOnVirtualScreen = false;
        platform.WarpCursor(hit.EntryX, hit.EntryY);
        platform.ShowCursor();
        log.LogInformation("Returned to real screen ← ({X}, {Y})", hit.EntryX, hit.EntryY);
    }

    // fills in screens with zero width/height using real display bounds,
    // and positions the virtual screen to the right of the real one
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
                // position virtual screen to the right of the real one
                var rx = realScreen?.X + (realScreen?.Width ?? primaryBounds.Width);
                result.Add(new ScreenRect(def.Name, rx ?? x, y, w, h, true));
            }
        }

        return result;
    }
}

using System.Runtime.Versioning;
using Hydra.Relay;
using Hydra.Screen;
using Microsoft.Extensions.Logging;

namespace Hydra.Platform.Windows;

[SupportedOSPlatform("windows")]
public sealed class WindowsOutputHandler(ILogger<WindowsOutputHandler> log, IScreenDetector screens) : IPlatformOutput, ICursorVisibility
{
    private readonly WindowsCursorSnapshot _cursor = new();
    private readonly DesktopInputDispatcher _dispatcher = new(log);
    private int _vLeft, _vTop, _vWidth, _vHeight;

    // refresh cached virtual screen metrics on screen config change
    public void Initialize()
    {
        RefreshMetrics();
        screens.ScreensChanged += _ => { RefreshMetrics(); return Task.CompletedTask; };
    }

    private void RefreshMetrics()
    {
        _vLeft = NativeMethods.GetSystemMetrics(NativeMethods.SM_XVIRTUALSCREEN);
        _vTop = NativeMethods.GetSystemMetrics(NativeMethods.SM_YVIRTUALSCREEN);
        _vWidth = NativeMethods.GetSystemMetrics(NativeMethods.SM_CXVIRTUALSCREEN);
        _vHeight = NativeMethods.GetSystemMetrics(NativeMethods.SM_CYVIRTUALSCREEN);
        if (_vWidth == 0) _vWidth = 1;
        if (_vHeight == 0) _vHeight = 1;
    }

    public void MoveMouse(int x, int y)
    {
        // normalize to 0-65535 across entire virtual desktop (all monitors)
        var dx = ((x - _vLeft) * 65536 + _vWidth / 2) / _vWidth;
        var dy = ((y - _vTop) * 65536 + _vHeight / 2) / _vHeight;

        _dispatcher.Dispatch(new MoveMouseCommand(dx, dy));
    }

    public void MoveMouseRelative(int dx, int dy)
    {
        _dispatcher.Dispatch(new MoveMouseRelativeCommand(dx, dy));
    }

    public void InjectKey(KeyEventMessage msg)
    {
        _dispatcher.Dispatch(new InjectKeyCommand(msg));
    }

    public void InjectMouseButton(MouseButtonMessage msg)
    {
        _dispatcher.Dispatch(new InjectMouseButtonCommand(msg));
    }

    public void InjectMouseScroll(MouseScrollMessage msg)
    {
        _dispatcher.Dispatch(new InjectMouseScrollCommand(msg));
    }

    public void HideCursor() => _cursor.Hide();

    public void ShowCursor() => _cursor.Show();

    public CursorPosition GetCursorPosition()
    {
        NativeMethods.GetCursorPos(out var pt);
        return new CursorPosition(pt.x, pt.y);
    }

    public void Dispose()
    {
        _dispatcher.Dispose();
        _cursor.Dispose();
    }
}

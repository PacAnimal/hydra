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
        // normalize to 0-65535 across entire virtual desktop (all monitors).
        // SendInput MOUSEEVENTF_ABSOLUTE: 0=left edge, 65535=right edge. divide by (width-1) so
        // pixel vWidth-1 maps exactly to 65535, not ~65519 (the 65536/width off-by-one error).
        var dw = Math.Max(1, _vWidth - 1);
        var dh = Math.Max(1, _vHeight - 1);
        var dx = ((x - _vLeft) * 65535 + dw / 2) / dw;
        var dy = ((y - _vTop) * 65535 + dh / 2) / dh;

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

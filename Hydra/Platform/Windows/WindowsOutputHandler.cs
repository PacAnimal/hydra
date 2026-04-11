using System.Runtime.Versioning;
using Hydra.Relay;
using Microsoft.Extensions.Logging;

namespace Hydra.Platform.Windows;

[SupportedOSPlatform("windows")]
public sealed class WindowsOutputHandler(ILogger<WindowsOutputHandler> log) : IPlatformOutput, ICursorVisibility
{
    private readonly WindowsCursorSnapshot _cursor = new();
    private readonly DesktopInputDispatcher _dispatcher = new(log);

    public void MoveMouse(int x, int y)
    {
        var vLeft = NativeMethods.GetSystemMetrics(NativeMethods.SM_XVIRTUALSCREEN);
        var vTop = NativeMethods.GetSystemMetrics(NativeMethods.SM_YVIRTUALSCREEN);
        var vWidth = NativeMethods.GetSystemMetrics(NativeMethods.SM_CXVIRTUALSCREEN);
        var vHeight = NativeMethods.GetSystemMetrics(NativeMethods.SM_CYVIRTUALSCREEN);
        if (vWidth == 0) vWidth = 1;
        if (vHeight == 0) vHeight = 1;

        // normalize to 0-65535 across entire virtual desktop (all monitors)
        var dx = ((x - vLeft) * 65536 + vWidth / 2) / vWidth;
        var dy = ((y - vTop) * 65536 + vHeight / 2) / vHeight;

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

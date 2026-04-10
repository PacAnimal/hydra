using System.Runtime.Versioning;
using Hydra.Relay;
using Microsoft.Extensions.Logging;

namespace Hydra.Platform.Windows;

[SupportedOSPlatform("windows")]
public sealed class WindowsOutputHandler(ILogger<WindowsOutputHandler> log) : IPlatformOutput, ICursorVisibility
{
    private bool _cursorHidden;
    private nint[]? _savedCursors;

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

    public unsafe void HideCursor()
    {
        if (_cursorHidden) return;

        // save copies of the current system cursors so we can restore them exactly
        // (SPI_SETCURSORS reloads from HKCU which maps to the wrong user when running under a winlogon token)
        var ids = NativeMethods.AllCursorIds;
        _savedCursors = new nint[ids.Length];
        for (var i = 0; i < ids.Length; i++)
        {
            var shared = NativeMethods.LoadCursor(nint.Zero, (nint)ids[i]);
            _savedCursors[i] = shared != nint.Zero ? NativeMethods.CopyCursor(shared) : nint.Zero;
        }

        byte andMask = 0xFF;
        byte xorMask = 0x00;
        var blank = NativeMethods.CreateCursor(nint.Zero, 0, 0, 1, 1, &andMask, &xorMask);
        if (blank == nint.Zero) return;
        foreach (var id in ids)
        {
            var copy = NativeMethods.CopyCursor(blank);
            if (copy != nint.Zero)
                NativeMethods.SetSystemCursor(copy, id);
        }
        NativeMethods.DestroyCursor(blank);
        _cursorHidden = true;
    }

    public void ShowCursor()
    {
        if (!_cursorHidden) return;
        RestoreSavedCursors();
        _cursorHidden = false;
    }

    private void RestoreSavedCursors()
    {
        if (_savedCursors == null) return;
        var ids = NativeMethods.AllCursorIds;
        for (var i = 0; i < ids.Length; i++)
        {
            if (_savedCursors[i] == nint.Zero) continue;
            // SetSystemCursor takes ownership; pass a fresh copy so we keep our saved handle
            var copy = NativeMethods.CopyCursor(_savedCursors[i]);
            if (copy != nint.Zero)
                NativeMethods.SetSystemCursor(copy, ids[i]);
        }
    }

    public CursorPosition GetCursorPosition()
    {
        NativeMethods.GetCursorPos(out var pt);
        return new CursorPosition(pt.x, pt.y);
    }

    public void Dispose()
    {
        _dispatcher.Dispose();
        if (_cursorHidden)
            RestoreSavedCursors();
        if (_savedCursors != null)
        {
            foreach (var h in _savedCursors)
                if (h != nint.Zero) NativeMethods.DestroyCursor(h);
            _savedCursors = null;
        }
    }
}

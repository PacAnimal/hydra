using System.ComponentModel;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Text;
using System.Runtime.CompilerServices;
using System.Runtime.Versioning;
using Microsoft.Extensions.Logging;

namespace Hydra.Platform.Windows;

// borderless per-pixel-alpha layered window that floats above everything else.
// displays outlined text centered horizontally, 10% from the bottom of the monitor the pointer is on.
// shows for 1.5s then fades out over 0.3s. click-through (WS_EX_TRANSPARENT).
[SupportedOSPlatform("windows")]
internal sealed class WindowsOsdNotification : IOsdNotification, IDisposable
{
    private readonly ILogger<WindowsOsdNotification> _log;
    private readonly StaMessageLoop _loop;
    private Win32Window? _window; // STA thread only

    private nint _hbmp;
    private int _bmpW, _bmpH, _posX, _posY;
    private int _fadeAlpha;

    private const nuint TimerId = 1;
    private const nuint FadeTimerId = 2;
    private const uint ShowDurationMs = 1500; // solid display before fade (matches Mac)
    private const uint FadeTickMs = 16;       // ~60fps
    private const int FadeStep = 14;          // 255/14 ≈ 18 ticks ≈ 0.29s fade (matches Mac 0.3s)

    public WindowsOsdNotification(ILogger<WindowsOsdNotification> log)
    {
        _log = log;
        _loop = new StaMessageLoop(
            "HydraOsd",
            init: CreateWindow,
            onExit: () =>
            {
                _window?.Dispose();
                _window = null;
                FreeHbmp();
            });
    }

    public void Show(string message) => _loop.Post(() => ShowOnSta(message));

    // the window's handle, zero if it could not be made
    private nint Hwnd => _window?.Handle ?? nint.Zero;

    private void CreateWindow()
    {
        // prime GDI+ font + rendering pipeline so the first real Show() is instant
        try { using var warm = RenderText(" ", 1f, out _, out _); } catch { /* ignore */ }

        const uint exStyle = NativeMethods.WS_EX_LAYERED
            | NativeMethods.WS_EX_TOPMOST
            | NativeMethods.WS_EX_TOOLWINDOW
            | NativeMethods.WS_EX_NOACTIVATE
            | NativeMethods.WS_EX_TRANSPARENT;
        try { _window = new Win32Window("HydraOsd", WndProcImpl, NativeMethods.WS_POPUP, exStyle); }
        catch (Win32Exception ex) { _log.LogWarning(ex, "Could not create the OSD window; notifications will not show"); }
    }

    private void ShowOnSta(string text)
    {
        if (Hwnd == nint.Zero) return;

        NativeMethods.KillTimer(Hwnd, TimerId);
        NativeMethods.KillTimer(Hwnd, FadeTimerId);
        FreeHbmp();

        NativeMethods.GetCursorPos(out var cursor);
        var hMonitor = NativeMethods.MonitorFromPoint(cursor, NativeMethods.MONITOR_DEFAULTTONEAREST);
        var mi = new MONITORINFOEX { Size = (uint)Unsafe.SizeOf<MONITORINFOEX>() };
        NativeMethods.GetMonitorInfoW(hMonitor, ref mi);
        var mx = mi.Monitor.Left;
        var my = mi.Monitor.Top;
        var sw = mi.Monitor.Right - mi.Monitor.Left;
        var sh = mi.Monitor.Bottom - mi.Monitor.Top;

        // Fixed logical size, scaled by the dpi of the monitor the pointer is on - the same approach
        // macOS gets for free from points. Sizing off resolution made a rotated 720x1280 panel render
        // the OSD at nearly twice the size of the identical panel in landscape.
        using var bmp = RenderText(text, DpiScale(hMonitor), out var bmpW, out var bmpH);
        _hbmp = bmp.GetHbitmap(Color.FromArgb(0));
        _bmpW = bmpW;
        _bmpH = bmpH;
        _posX = mx + (sw - bmpW) / 2;
        _posY = my + sh - bmpH - (int)(sh * 0.1);

        NativeMethods.SetWindowPos(Hwnd, NativeMethods.HWND_TOPMOST, _posX, _posY, _bmpW, _bmpH,
            NativeMethods.SWP_NOACTIVATE | NativeMethods.SWP_SHOWWINDOW);
        DrawAlpha(255);

        NativeMethods.SetTimer(Hwnd, TimerId, ShowDurationMs, nint.Zero);
    }

    private void StartFadeOnSta()
    {
        NativeMethods.KillTimer(Hwnd, TimerId);
        _fadeAlpha = 255;
        NativeMethods.SetTimer(Hwnd, FadeTimerId, FadeTickMs, nint.Zero);
    }

    private void FadeTickOnSta()
    {
        _fadeAlpha -= FadeStep;
        if (_fadeAlpha <= 0) { HideOnSta(); return; }
        DrawAlpha((byte)_fadeAlpha);
    }

    // re-apply UpdateLayeredWindow with a new SourceConstantAlpha to animate the fade
    private void DrawAlpha(byte alpha)
    {
        if (_hbmp == nint.Zero) return;
        var dstPt = new WINPOINT { x = _posX, y = _posY };
        var srcPt = new WINPOINT { x = 0, y = 0 };
        var size = new NativeMethods.WINSIZE { cx = _bmpW, cy = _bmpH };
        var blend = new NativeMethods.BLENDFUNCTION
        {
            BlendOp = NativeMethods.AC_SRC_OVER,
            SourceConstantAlpha = alpha,
            AlphaFormat = NativeMethods.AC_SRC_ALPHA,
        };
        var screenDc = NativeMethods.GetDC(nint.Zero);
        var memDc = NativeMethods.CreateCompatibleDC(screenDc);
        var oldBmp = NativeMethods.SelectObject(memDc, _hbmp);
        NativeMethods.UpdateLayeredWindow(Hwnd, nint.Zero, ref dstPt, ref size, memDc, ref srcPt, 0, ref blend, NativeMethods.ULW_ALPHA);
        NativeMethods.SelectObject(memDc, oldBmp);
        NativeMethods.DeleteDC(memDc);
        _ = NativeMethods.ReleaseDC(nint.Zero, screenDc);
    }

    private void HideOnSta()
    {
        if (Hwnd == nint.Zero) return;
        NativeMethods.KillTimer(Hwnd, TimerId);
        NativeMethods.KillTimer(Hwnd, FadeTimerId);
        NativeMethods.ShowWindow(Hwnd, NativeMethods.SW_HIDE);
        FreeHbmp();
    }

    private void FreeHbmp()
    {
        if (_hbmp == nint.Zero) return;
        NativeMethods.DeleteObject(_hbmp);
        _hbmp = nint.Zero;
    }

    // Logical em size, in the same spirit as the macOS OSD's fixed point size. Multiplied by the
    // monitor's dpi scale, since this process is PerMonitorV2 aware and GDI+ renders into a 96 dpi
    // bitmap: without that a 4K display at 150% would show a noticeably smaller OSD.
    // 28pt, the same size the macOS OSD uses (main.swift, systemFont ofSize: 28, .bold), so the two
    // platforms match by construction rather than by a tuned Windows-only number.
    private const float BaseEmSize = 28f;

    // 96 dpi is USER_DEFAULT_SCREEN_DPI, i.e. 100% scaling. Fall back to that if the query fails,
    // which leaves the OSD at its plain logical size rather than an arbitrary one.
    private static float DpiScale(nint hMonitor)
    {
        if (NativeMethods.GetDpiForMonitor(hMonitor, NativeMethods.MDT_EFFECTIVE_DPI, out var dpiX, out _) != 0)
            return 1f;
        return dpiX == 0 ? 1f : dpiX / 96f;
    }

    private static Bitmap RenderText(string text, float dpiScale, out int width, out int height)
    {
        var emSize = BaseEmSize * dpiScale;
        var padding = (int)(emSize * 0.6f);

        // measure first on a throwaway bitmap
        using var measure = new Bitmap(1, 1);
        using var gm = Graphics.FromImage(measure);
        var fontFamily = new FontFamily("Segoe UI");
        using var path = new GraphicsPath();
        path.AddString(text, fontFamily, (int)FontStyle.Bold, emSize, Point.Empty, StringFormat.GenericDefault);
        var bounds = path.GetBounds();

        width = (int)Math.Ceiling(bounds.Width) + padding * 2;
        height = (int)Math.Ceiling(bounds.Height) + padding * 2;

        var bmp = new Bitmap(width, height, System.Drawing.Imaging.PixelFormat.Format32bppArgb);
        using var g = Graphics.FromImage(bmp);
        g.SmoothingMode = SmoothingMode.HighQuality;
        g.TextRenderingHint = TextRenderingHint.AntiAliasGridFit;
        g.Clear(Color.Transparent);

        using var renderPath = new GraphicsPath();
        var origin = new PointF(padding - bounds.X, padding - bounds.Y);
        renderPath.AddString(text, fontFamily, (int)FontStyle.Bold, emSize, origin, StringFormat.GenericDefault);

        // multi-pass graduated glow — approximates a blurred shadow (GDI+ has no blur)
        // outermost pass is widest and most transparent; each inner pass narrows and darkens
        (float widthFactor, int alpha)[] glowPasses = [(0.20f, 40), (0.13f, 80), (0.08f, 140), (0.05f, 200)];
        foreach (var (wf, alpha) in glowPasses)
        {
            using var pen = new Pen(Color.FromArgb(alpha, 0, 0, 0), emSize * wf);
            pen.LineJoin = LineJoin.Round;
            g.DrawPath(pen, renderPath);
        }

        // near-white fill
        using var fillBrush = new SolidBrush(Color.FromArgb(240, 242, 242, 242));
        g.FillPath(fillBrush, renderPath);

        // hard 1px outline on top of fill
        using var outlinePen = new Pen(Color.FromArgb(200, 0, 0, 0), 1.0f);
        outlinePen.LineJoin = LineJoin.Round;
        g.DrawPath(outlinePen, renderPath);

        fontFamily.Dispose();
        return bmp;
    }

    private nint WndProcImpl(nint hWnd, uint msg, nint wParam, nint lParam)
    {
        if (msg == NativeMethods.WM_TIMER)
        {
            if ((nuint)wParam == TimerId) StartFadeOnSta();
            else if ((nuint)wParam == FadeTimerId) FadeTickOnSta();
            return nint.Zero;
        }
        return NativeMethods.DefWindowProcW(hWnd, msg, wParam, lParam);
    }

    public void Dispose() => _loop.Dispose();
}

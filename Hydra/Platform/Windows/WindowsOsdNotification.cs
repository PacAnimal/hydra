using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Text;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;

namespace Hydra.Platform.Windows;

// borderless per-pixel-alpha layered window that floats above everything else.
// displays outlined text centered horizontally, 10% from the bottom of the primary screen.
// shows for 1.5s then fades out over 0.3s. click-through (WS_EX_TRANSPARENT).
[SupportedOSPlatform("windows")]
internal sealed class WindowsOsdNotification : IOsdNotification, IDisposable
{
    private readonly StaMessageLoop _loop;
    private nint _hwnd;
    private WndProc? _wndProc;

    private nint _hbmp;
    private int _bmpW, _bmpH, _posX, _posY;
    private int _fadeAlpha;

    private const nuint TimerId = 1;
    private const nuint FadeTimerId = 2;
    private const uint ShowDurationMs = 1500; // solid display before fade (matches Mac)
    private const uint FadeTickMs = 16;       // ~60fps
    private const int FadeStep = 14;          // 255/14 ≈ 18 ticks ≈ 0.29s fade (matches Mac 0.3s)
    private const uint WmShowOsd = NativeMethods.WM_USER + 61;

    public WindowsOsdNotification()
    {
        _loop = new StaMessageLoop(
            "HydraOsd",
            init: CreateWindow,
            onThreadMessage: HandleThreadMessage);
    }

    public void Show(string message)
    {
        var handle = GCHandle.Alloc(message);
        if (!NativeMethods.PostThreadMessage(_loop.ThreadId, WmShowOsd, nint.Zero, GCHandle.ToIntPtr(handle)))
            handle.Free();
    }

    private bool HandleThreadMessage(MSG msg)
    {
        if (msg.message == WmShowOsd)
        {
            var handle = GCHandle.FromIntPtr(msg.lParam);
            var text = (string?)handle.Target ?? "";
            handle.Free();
            ShowOnSta(text);
            return true;
        }
        if (msg.hwnd == nint.Zero && msg.message == NativeMethods.WM_TIMER)
        {
            if ((nuint)msg.wParam == TimerId) { StartFadeOnSta(); return true; }
            if ((nuint)msg.wParam == FadeTimerId) { FadeTickOnSta(); return true; }
        }
        return false;
    }

    private void CreateWindow()
    {
        _wndProc = WndProcImpl;
        var hInstance = NativeMethods.GetModuleHandleW(nint.Zero);
        var className = Marshal.StringToHGlobalUni("HydraOsd");
        try
        {
            var wc = new NativeMethods.WNDCLASSEXW
            {
                cbSize = (uint)Marshal.SizeOf<NativeMethods.WNDCLASSEXW>(),
                lpfnWndProc = Marshal.GetFunctionPointerForDelegate(_wndProc),
                hInstance = hInstance,
                lpszClassName = className,
            };
            var atom = NativeMethods.RegisterClassExW(in wc);
            if (atom == 0) return;

            var exStyle = NativeMethods.WS_EX_LAYERED
                | NativeMethods.WS_EX_TOPMOST
                | NativeMethods.WS_EX_TOOLWINDOW
                | NativeMethods.WS_EX_NOACTIVATE
                | NativeMethods.WS_EX_TRANSPARENT;

            _hwnd = NativeMethods.CreateWindowExW(
                exStyle, atom, nint.Zero,
                NativeMethods.WS_POPUP,
                0, 0, 1, 1,
                nint.Zero, nint.Zero, hInstance, nint.Zero);
        }
        finally
        {
            Marshal.FreeHGlobal(className);
        }
    }

    private void ShowOnSta(string text)
    {
        if (_hwnd == nint.Zero) return;

        NativeMethods.KillTimer(_hwnd, TimerId);
        NativeMethods.KillTimer(_hwnd, FadeTimerId);
        FreeHbmp();

        var sw = NativeMethods.GetSystemMetrics(NativeMethods.SM_CXSCREEN);
        var sh = NativeMethods.GetSystemMetrics(NativeMethods.SM_CYSCREEN);

        using var bmp = RenderText(text, sh, out var bmpW, out var bmpH);
        _hbmp = bmp.GetHbitmap(Color.FromArgb(0));
        _bmpW = bmpW;
        _bmpH = bmpH;
        _posX = (sw - bmpW) / 2;
        _posY = sh - bmpH - (int)(sh * 0.1);

        NativeMethods.SetWindowPos(_hwnd, NativeMethods.HWND_TOPMOST, _posX, _posY, _bmpW, _bmpH,
            NativeMethods.SWP_NOACTIVATE | NativeMethods.SWP_SHOWWINDOW);
        DrawAlpha(255);

        NativeMethods.SetTimer(_hwnd, TimerId, ShowDurationMs, nint.Zero);
    }

    private void StartFadeOnSta()
    {
        NativeMethods.KillTimer(_hwnd, TimerId);
        _fadeAlpha = 255;
        NativeMethods.SetTimer(_hwnd, FadeTimerId, FadeTickMs, nint.Zero);
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
        NativeMethods.UpdateLayeredWindow(_hwnd, nint.Zero, ref dstPt, ref size, memDc, ref srcPt, 0, ref blend, NativeMethods.ULW_ALPHA);
        NativeMethods.SelectObject(memDc, oldBmp);
        NativeMethods.DeleteDC(memDc);
        _ = NativeMethods.ReleaseDC(nint.Zero, screenDc);
    }

    private void HideOnSta()
    {
        if (_hwnd == nint.Zero) return;
        NativeMethods.KillTimer(_hwnd, TimerId);
        NativeMethods.KillTimer(_hwnd, FadeTimerId);
        NativeMethods.ShowWindow(_hwnd, NativeMethods.SW_HIDE);
        FreeHbmp();
    }

    private void FreeHbmp()
    {
        if (_hbmp == nint.Zero) return;
        NativeMethods.DeleteObject(_hbmp);
        _hbmp = nint.Zero;
    }

    private static Bitmap RenderText(string text, int sh, out int width, out int height)
    {
        // scale font to ~3% of screen height for readability on any resolution
        var emSize = Math.Max(18f, sh * 0.03f);
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

    public void Dispose()
    {
        if (_hwnd != nint.Zero) { NativeMethods.DestroyWindow(_hwnd); _hwnd = nint.Zero; }
        FreeHbmp();
        _loop.Dispose();
    }
}

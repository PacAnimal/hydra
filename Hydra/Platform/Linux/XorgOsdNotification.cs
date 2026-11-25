using System.Collections.Concurrent;
using System.Globalization;
using System.Runtime.InteropServices;
using Hydra.Screen;
using Microsoft.Extensions.Logging;
using static Hydra.Platform.Linux.CairoNativeMethods;

namespace Hydra.Platform.Linux;

// the OSD on an X11 display, to OsdStyle as macOS and Windows show it: outlined text, centred on the screen the pointer
// is on, 10% of its height from the bottom, shown then faded, with or without a compositor. a borderless
// override-redirect window the pointer clicks through (an empty input shape), its picture in its background pixmap,
// so the server repaints it with no help. where a compositor runs (a _NET_WM_CM_Sn owner) it is a 32-bit ARGB window,
// faded by the compositor through _NET_WM_WINDOW_OPACITY. with none, the server blends nothing: the window is shaped
// to the text's inked pixels, and each frame of the show and the fade is the text blended by cairo over a copy of the
// screen taken under it before it was mapped (where a notice replaces one still showing, the old one's own copy fills
// in what it covered). what changes under the text while it shows is not seen through it.
// every X call runs on the OSD's own thread and connection; a display that cannot be opened, or no cairo, XFIXES or
// SHAPE, leaves the OSD silent, logged once at its start. a notice that fails is logged and dropped, and the OSD
// carries on.
internal sealed class XorgOsdNotification : IOsdNotification, IDisposable
{
    private const string WindowName = "Hydra OSD";
    // cairo's image limit is 32767 a side, and a shape rectangle's is a short; the widest notice is far below either
    private const int MaxChars = 200;

    private readonly ILogger<XorgOsdNotification> _log;
    private readonly TimeSpan _showFor;
    private readonly TimeSpan _fadeFor;
    private readonly BlockingCollection<string> _messages = [];
    private int _disposed;
    private readonly Thread _thread;

    // OSD thread only
    private nint _display;
    private nint _window;
    private nint _pixmap;
    private nint _colormap;
    // no compositor: the screen under the window, the window's visual, and the picture it shows
    private nint _under, _visual;
    private ScreenPoint _at;
    private OsdImage? _image;
    private bool _composited;
    private nint _windowType, _notificationType, _opacity;
    private long _hideAt, _fadeStart;

    // for tests: the window just mapped, and the window just hidden; raised on the OSD thread
    internal event Action<nint>? Shown;
    internal event Action<nint>? Hidden;
    // for tests: each opacity the window is shown at, 0 to 1, once the server has the request that shows it; raised on
    // the OSD thread, with the window and (no compositor) the pixmap holding the screen under it
    internal event Action<OsdFrame>? Frame;


    public XorgOsdNotification(ILogger<XorgOsdNotification> log) : this(log, OsdStyle.ShowFor, OsdStyle.FadeFor) { }

    internal XorgOsdNotification(ILogger<XorgOsdNotification> log, TimeSpan showFor, TimeSpan fadeFor)
    {
        _log = log;
        _showFor = showFor;
        _fadeFor = fadeFor;
        _thread = new Thread(Run) { IsBackground = true, Name = "HydraOsd" };
        _thread.Start();
    }

    public void Show(string message)
    {
        try { _messages.Add(message); }
        catch (Exception ex) when (ex is InvalidOperationException or ObjectDisposedException) { /* disposed */ }
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        _messages.CompleteAdding();
        // the queue is left to the collector if the thread is still running, so the thread never meets a disposed one
        if (_thread.Join(TimeSpan.FromSeconds(2))) _messages.Dispose();
    }

    private void Run()
    {
        _display = XlibRuntime.TryOpenDisplay();
        if (_display == nint.Zero)
        {
            _log.LogWarning("Could not open the X11 display for the OSD; notifications will not show");
            Drain();
            return;
        }

        try
        {
            string? missing;
            try { missing = Unavailable(); }
            catch (Exception ex) { missing = $"a working cairo ({ex.Message})"; }
            if (missing != null)
            {
                _log.LogWarning("The OSD needs {Missing}; notifications will not show", missing);
                return;
            }
            _windowType = NativeMethods.XInternAtom(_display, "_NET_WM_WINDOW_TYPE", false);
            _notificationType = NativeMethods.XInternAtom(_display, "_NET_WM_WINDOW_TYPE_NOTIFICATION", false);
            _opacity = NativeMethods.XInternAtom(_display, "_NET_WM_WINDOW_OPACITY", false);

            while (!_messages.IsCompleted)
            {
                if (!_messages.TryTake(out var message, Wait())) Step(Tick);
                else
                {
                    // a burst of notices shows only the newest
                    while (_messages.TryTake(out var newer)) message = newer;
                    Step(() => ShowNow(message));
                }
            }
        }
        catch (InvalidOperationException) when (Volatile.Read(ref _disposed) != 0)
        {
            // disposed while waiting
        }
        finally
        {
            DestroyWindow();
            _ = NativeMethods.XCloseDisplay(_display);
            _display = nint.Zero;
            Drain();
        }
    }

    // a notice that fails is dropped with its window; the next one starts clean
    private void Step(Action step)
    {
        try { step(); }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "The OSD failed to show a notice");
            try { DestroyWindow(); }
            catch (Exception cleanup) { _log.LogDebug(cleanup, "The OSD could not remove its window"); }
        }
    }

    // what the OSD lacks to show anything: cairo, or the X server's XFIXES and SHAPE (click-through, and the shape that
    // keeps an uncomposited window to its text); null when it has them all
    private string? Unavailable()
    {
        try
        {
            using var probe = OsdImage.Render("x", OsdStyle.EmSize);
        }
        catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException)
        {
            return "cairo (libcairo.so.2)";
        }
        if (!NativeMethods.XFixesQueryExtension(_display, out _, out _)) return "the X server's XFIXES extension";
        if (!NativeMethods.XQueryExtension(_display, "SHAPE", out _, out _, out _)) return "the X server's SHAPE extension";
        return null;
    }

    // after the OSD has stopped, Show still adds until Dispose: take them, so they are not kept
    private void Drain()
    {
        try { foreach (var _ in _messages.GetConsumingEnumerable()) { } }
        catch (ObjectDisposedException) { }
    }

    // how long to wait for a message before the next tick: forever while nothing shows
    private int Wait()
    {
        if (_window == nint.Zero) return Timeout.Infinite;
        if (_fadeStart != 0) return (int)OsdStyle.FadeTick.TotalMilliseconds;
        return (int)Math.Max(0, _hideAt - Environment.TickCount64);
    }

    private void Tick()
    {
        if (_window == nint.Zero) return;
        var now = Environment.TickCount64;
        if (_fadeStart == 0)
        {
            if (now < _hideAt) return;
            if (_fadeFor <= TimeSpan.Zero) { Hide(); return; }
            // the first fade step is one tick in, so every step is below the full opacity it was shown at
            _fadeStart = now - (long)OsdStyle.FadeTick.TotalMilliseconds;
        }

        var left = 1 - (now - _fadeStart) / _fadeFor.TotalMilliseconds;
        if (left <= 0) { Hide(); return; }
        SetOpacity(left);
    }

    private void ShowNow(string text)
    {
        var screenNumber = NativeMethods.XDefaultScreen(_display);
        var root = NativeMethods.XDefaultRootWindow(_display);
        // pixels at 96 dpi, scaled by Xft.dpi
        var emSize = OsdStyle.EmSize * DpiScale();
        if (text.Length > MaxChars) text = text[..(char.IsHighSurrogate(text[MaxChars - 2]) ? MaxChars - 2 : MaxChars - 1)] + "…";
        using var image = OsdImage.Render(text, emSize);
        // a notice that cannot show still takes down the one it replaces
        if (image == null) { DestroyWindow(); return; }

        var pointer = XorgDisplayHelper.CursorPosition(_display, root) ?? new ScreenPoint(0, 0);
        var screens = XorgDisplayHelper.GetAllScreens(_display, root).Select(s => s.Bounds).ToList();
        if (OsdLayout.ScreenAt(screens, pointer) is not { } screen) { DestroyWindow(); return; }
        var at = OsdStyle.Place(screen, image.Width, image.Height);

        var hasArgb = NativeMethods.XMatchVisualInfo(_display, screenNumber, 32, NativeMethods.TrueColor, out var argb) != 0;
        var composited = hasArgb && Composited(screenNumber);
        var depth = composited ? 32 : NativeMethods.XDefaultDepth(_display, screenNumber);
        var (width, height) = ((uint)image.Width, (uint)image.Height);

        // the screen under the new window, taken while a notice it replaces still covers its part of it: its pixels
        // there come from that notice's own copy, since the clients under it have not yet repainted what it hid
        var under = composited ? nint.Zero : ScreenUnder(root, at, width, height, (uint)depth);
        DestroyWindow();

        _composited = composited;
        _visual = composited ? argb.Visual : NativeMethods.XDefaultVisual(_display, screenNumber);
        _under = under;
        _at = at;
        // the ARGB visual needs its own colormap; the default visual takes the screen's
        if (composited) _colormap = NativeMethods.XCreateColormap(_display, root, _visual, NativeMethods.AllocNone);
        var attributes = new XSetWindowAttributes { OverrideRedirect = 1, Colormap = composited ? _colormap : NativeMethods.XDefaultColormap(_display, screenNumber) };
        _window = NativeMethods.XCreateWindow(_display, root, at.X, at.Y, width, height, 0, depth,
            NativeMethods.InputOutput, _visual,
            NativeMethods.CWOverrideRedirect | NativeMethods.CWColormap | NativeMethods.CWBorderPixel | NativeMethods.CWBackPixel,
            ref attributes);
        if (_window == nint.Zero) return;
        _ = NativeMethods.XStoreName(_display, _window, WindowName);
        SetWindowType();
        SetShape(NativeMethods.ShapeInput, []);
        _pixmap = NativeMethods.XCreatePixmap(_display, _window, width, height, (uint)depth);

        // the first frame is drawn before the pixmap becomes the background: drawing into a background pixmap after
        // it is set is undefined (the server may have copied it)
        if (composited) image.PaintOnto(_display, _pixmap, _visual, under: nint.Zero, alpha: 1);
        else
        {
            SetShape(NativeMethods.ShapeBounding, [.. OsdLayout.InkRuns(image.Pixels(), image.Width, image.Height, image.Stride)]);
            _image = image.Keep();
            _image.PaintOnto(_display, _pixmap, _visual, _under, 1);
        }
        _ = NativeMethods.XSetWindowBackgroundPixmap(_display, _window, _pixmap);
        if (composited) SetOpacityProperty(1);

        _ = NativeMethods.XMapWindow(_display, _window);
        _ = NativeMethods.XRaiseWindow(_display, _window);
        _ = NativeMethods.XSync(_display, false);
        _fadeStart = 0;
        _hideAt = Environment.TickCount64 + (long)_showFor.TotalMilliseconds;
        Frame?.Invoke(new OsdFrame(_window, _under, 1));
        Shown?.Invoke(_window);
    }

    // a new pixmap holding the screen at the rectangle, with what a showing uncomposited notice covers taken from its
    // copy of the screen
    private nint ScreenUnder(nint root, ScreenPoint at, uint width, uint height, uint depth)
    {
        var under = NativeMethods.XCreatePixmap(_display, root, width, height, depth);
        var gc = NativeMethods.XCreateGC(_display, root, 0, nint.Zero);
        try
        {
            // no GraphicsExpose/NoExpose events: this connection never reads them, and they would pile up in its queue
            _ = NativeMethods.XSetGraphicsExposures(_display, gc, false);
            _ = NativeMethods.XSetSubwindowMode(_display, gc, NativeMethods.IncludeInferiors);
            _ = NativeMethods.XCopyArea(_display, root, under, gc, at.X, at.Y, width, height, 0, 0);
            // a source rectangle past the old copy's edges copies only the part inside it
            if (_window != nint.Zero && _under != nint.Zero)
                _ = NativeMethods.XCopyArea(_display, _under, under, gc, at.X - _at.X, at.Y - _at.Y, width, height, 0, 0);
        }
        finally
        {
            _ = NativeMethods.XFreeGC(_display, gc);
        }
        return under;
    }

    private void Hide()
    {
        var window = _window;
        DestroyWindow();
        _ = NativeMethods.XSync(_display, false);
        Hidden?.Invoke(window);
    }

    private void DestroyWindow()
    {
        _fadeStart = 0;
        if (_window != nint.Zero) _ = NativeMethods.XDestroyWindow(_display, _window);
        if (_pixmap != nint.Zero) _ = NativeMethods.XFreePixmap(_display, _pixmap);
        if (_colormap != nint.Zero) _ = NativeMethods.XFreeColormap(_display, _colormap);
        if (_under != nint.Zero) _ = NativeMethods.XFreePixmap(_display, _under);
        _image?.Dispose();
        _image = null;
        _window = _pixmap = _colormap = _under = nint.Zero;
    }

    // a compositor owns _NET_WM_CM_S<screen> while it runs (EWMH)
    private bool Composited(int screenNumber)
    {
        var selection = NativeMethods.XInternAtom(_display, $"_NET_WM_CM_S{screenNumber}", false);
        return NativeMethods.XGetSelectionOwner(_display, selection) != nint.Zero;
    }

    // Xft.dpi, as the desktop had it when this connection first read its resources, over 96; 1 when unset or
    // unreadable. Xlib keeps that first read, so a later change to Xft.dpi takes a Hydra restart
    private double DpiScale()
    {
        var value = NativeMethods.XGetDefault(_display, "Xft", "dpi");
        return value != nint.Zero
               && double.TryParse(Marshal.PtrToStringUTF8(value), NumberStyles.Float, CultureInfo.InvariantCulture, out var dpi)
               && dpi > 0
            ? dpi / 96
            : 1;
    }

    // a notification, so compositors and their rules treat it as one
    private void SetWindowType() =>
        _ = NativeMethods.XChangeProperty(_display, _window, _windowType, NativeMethods.XA_ATOM, 32, NativeMethods.PropModeReplace, [_notificationType], 1);

    // with a compositor, the window's opacity property; without, the frame drawn again at that alpha over the copy of
    // the screen, set as the background again and the window repainted from it
    private void SetOpacity(double opacity)
    {
        opacity = Math.Clamp(opacity, 0, 1);
        if (_composited) SetOpacityProperty(opacity);
        else if (_image != null)
        {
            _image.PaintOnto(_display, _pixmap, _visual, _under, opacity);
            _ = NativeMethods.XSetWindowBackgroundPixmap(_display, _window, _pixmap);
            _ = NativeMethods.XClearWindow(_display, _window);
        }
        _ = NativeMethods.XSync(_display, false);
        Frame?.Invoke(new OsdFrame(_window, _under, opacity));
    }

    private void SetOpacityProperty(double opacity)
    {
        var value = (nint)(uint)Math.Round(opacity * uint.MaxValue);
        _ = NativeMethods.XChangeProperty(_display, _window, _opacity, NativeMethods.XA_CARDINAL, 32, NativeMethods.PropModeReplace, [value], 1);
    }

    private void SetShape(int kind, XRectangle[] rectangles)
    {
        var region = NativeMethods.XFixesCreateRegion(_display, rectangles, rectangles.Length);
        NativeMethods.XFixesSetWindowShapeRegion(_display, _window, kind, 0, 0, region);
        NativeMethods.XFixesDestroyRegion(_display, region);
    }
}

// one frame the OSD showed: its window, the pixmap with the screen under it (zero with a compositor) and the opacity
internal readonly record struct OsdFrame(nint Window, nint Under, double Opacity);

// the OSD's text drawn by cairo on a transparent ARGB32 image, in OsdStyle's passes: a graduated dark glow, a
// near-white fill and a hard outline
internal sealed class OsdImage : IDisposable
{
    private nint _surface;

    internal int Width { get; }
    internal int Height { get; }
    internal int Stride => cairo_image_surface_get_stride(_surface);

    private OsdImage(nint surface, int width, int height)
    {
        _surface = surface;
        Width = width;
        Height = height;
    }

    // null for text with no ink
    internal static OsdImage? Render(string text, double emSize)
    {
        var padding = OsdStyle.PaddingFor(emSize);

        var (x1, y1, x2, y2) = Extents(text, emSize);
        if (x2 <= x1 || y2 <= y1) return null;
        var width = (int)Math.Ceiling(x2 - x1) + padding * 2;
        var height = (int)Math.Ceiling(y2 - y1) + padding * 2;

        var surface = cairo_image_surface_create(CAIRO_FORMAT_ARGB32, width, height);
        var cr = cairo_create(surface);
        try
        {
            if (cairo_status(cr) != CAIRO_STATUS_SUCCESS) throw new InvalidOperationException("cairo could not draw the OSD");
            Path(cr, text, emSize, padding - x1, padding - y1);
            cairo_set_line_join(cr, CAIRO_LINE_JOIN_ROUND);
            foreach (var (widthFactor, alpha) in OsdStyle.GlowPasses)
            {
                cairo_set_source_rgba(cr, 0, 0, 0, alpha / 255.0);
                cairo_set_line_width(cr, emSize * widthFactor);
                cairo_stroke_preserve(cr);
            }
            const double grey = OsdStyle.FillGrey / 255.0;
            cairo_set_source_rgba(cr, grey, grey, grey, OsdStyle.FillAlpha / 255.0);
            cairo_fill_preserve(cr);
            cairo_set_source_rgba(cr, 0, 0, 0, OsdStyle.OutlineAlpha / 255.0);
            cairo_set_line_width(cr, 1);
            cairo_stroke(cr);
        }
        catch
        {
            cairo_surface_destroy(surface);
            throw;
        }
        finally
        {
            cairo_destroy(cr);
        }
        cairo_surface_flush(surface);
        return new OsdImage(surface, width, height);
    }

    // the image's bytes, row after row of Stride bytes
    internal byte[] Pixels()
    {
        var bytes = new byte[Stride * Height];
        Marshal.Copy(cairo_image_surface_get_data(_surface), bytes, 0, bytes.Length);
        return bytes;
    }

    // the image draws into an X drawable of the visual at the alpha: over the drawable `under` (of the same visual and
    // size) as it is, or, with none, onto transparency (an ARGB drawable a compositor blends)
    internal void PaintOnto(nint display, nint drawable, nint visual, nint under, double alpha)
    {
        var target = cairo_xlib_surface_create(display, drawable, visual, Width, Height);
        var cr = cairo_create(target);
        var background = under == nint.Zero ? nint.Zero : cairo_xlib_surface_create(display, under, visual, Width, Height);
        try
        {
            cairo_set_operator(cr, CAIRO_OPERATOR_SOURCE);
            if (background == nint.Zero) cairo_set_source_rgba(cr, 0, 0, 0, 0);
            else cairo_set_source_surface(cr, background, 0, 0);
            cairo_paint(cr);
            cairo_set_operator(cr, CAIRO_OPERATOR_OVER);
            cairo_set_source_surface(cr, _surface, 0, 0);
            cairo_paint_with_alpha(cr, alpha);
            cairo_surface_flush(target);
        }
        finally
        {
            cairo_destroy(cr);
            if (background != nint.Zero) cairo_surface_destroy(background);
            cairo_surface_destroy(target);
        }
    }

    // a second owner of the same image, disposed apart from this one
    internal OsdImage Keep() => new(cairo_surface_reference(_surface), Width, Height);

    public void Dispose()
    {
        if (_surface == nint.Zero) return;
        cairo_surface_destroy(_surface);
        _surface = nint.Zero;
    }

    private static (double X1, double Y1, double X2, double Y2) Extents(string text, double emSize)
    {
        var surface = cairo_image_surface_create(CAIRO_FORMAT_ARGB32, 1, 1);
        var cr = cairo_create(surface);
        try
        {
            Path(cr, text, emSize, 0, 0);
            cairo_path_extents(cr, out var x1, out var y1, out var x2, out var y2);
            return (x1, y1, x2, y2);
        }
        finally
        {
            cairo_destroy(cr);
            cairo_surface_destroy(surface);
        }
    }

    private static void Path(nint cr, string text, double emSize, double x, double y)
    {
        cairo_select_font_face(cr, "sans-serif", CAIRO_FONT_SLANT_NORMAL, CAIRO_FONT_WEIGHT_BOLD);
        cairo_set_font_size(cr, emSize);
        cairo_move_to(cr, x, y);
        cairo_text_path(cr, text);
    }
}

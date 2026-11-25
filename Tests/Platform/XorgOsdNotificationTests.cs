using Hydra.Platform.Linux;
using Microsoft.Extensions.Logging.Abstractions;
using Tests.Setup;

namespace Tests.Platform;

// the X11 OSD against a live X server: a window over the screen, clicked through, faded out and gone after its time;
// faded by the compositor where one owns _NET_WM_CM_S0, shaped to its text and faded by its own frames where none does
[TestFixture]
[Category("Linux")]
[NonParallelizable]
public class XorgOsdNotificationTests
{
    // long enough that a stalled runner never sees the window go while a test reads it; Dispose takes it down
    private static readonly TimeSpan Held = TimeSpan.FromMinutes(5);
    private static readonly TimeSpan ShowFor = TimeSpan.FromMilliseconds(300);
    // several fade ticks long, so even a stalled runner sets an opacity between full and gone
    private static readonly TimeSpan FadeFor = TimeSpan.FromSeconds(1);

    private nint _display;
    private nint _compositor;
    private nint _backdrop;
    private bool _rootPainted;

    [OneTimeSetUp]
    public void RequireX11() => X11Required.OrIgnore();

    [SetUp]
    public void OpenDisplay() => _display = XlibRuntime.OpenDisplayOrThrow("OSD tests");

    [TearDown]
    public void CloseDisplay()
    {
        foreach (var window in (nint[])[_compositor, _backdrop])
            if (window != nint.Zero) _ = NativeMethods.XDestroyWindow(_display, window);
        _compositor = _backdrop = nint.Zero;
        if (_rootPainted) _ = NativeMethods.XClearWindow(_display, NativeMethods.XDefaultRootWindow(_display));
        _rootPainted = false;
        if (_display != nint.Zero) _ = NativeMethods.XCloseDisplay(_display);
        _display = nint.Zero;
    }

    [Test]
    public async Task WithoutACompositor_ItShowsShapedToItsText_ClickThrough()
    {
        using var osd = new XorgOsdNotification(NullLogger<XorgOsdNotification>.Instance, Held, FadeFor);
        var (shown, _) = Watch(osd);

        osd.Show("Cursor lock: On");
        var window = await shown.Task.WaitAsync(TestBound.Bound);

        using (Assert.EnterMultipleScope())
        {
            var attributes = Attributes(window);
            Assert.That(attributes.MapState, Is.EqualTo(NativeMethods.IsViewable));
            Assert.That(attributes.OverrideRedirect, Is.Not.Zero);
            Assert.That(attributes.Depth, Is.Not.EqualTo(32), "no compositor blends alpha, so the window is opaque");
            Assert.That(OnScreen(attributes), Is.True);
            Assert.That(ShapeRectangles(window, NativeMethods.ShapeInput), Is.Zero, "the pointer clicks through");
            Assert.That(ShapeRectangles(window, NativeMethods.ShapeBounding), Is.GreaterThan(1), "shaped to the text");
            Assert.That(OpacityOf(window), Is.Null, "no compositor reads the property");
        }
    }

    // with nothing to blend it, the OSD blends its text over a copy of the screen under it, frame by frame: its pixels
    // start as the text and end as the screen
    [Test]
    public async Task WithoutACompositor_ItFadesIntoTheScreenUnderIt_BeforeItGoes()
    {
        const uint background = 0x3A6EA5;
        MapBackdrop(background);

        using var osd = new XorgOsdNotification(NullLogger<XorgOsdNotification>.Instance, ShowFor, FadeFor);
        var (shown, hidden) = Watch(osd);
        var frames = Record(osd, frame => OnScreenDistanceFrom(frame.Window, background));

        osd.Show("Cursor lock: Off");
        var shownWindow = await shown.Task.WaitAsync(TestBound.Bound);
        Assert.That(await hidden.Task.WaitAsync(TestBound.Bound), Is.EqualTo(shownWindow));

        var seen = frames.Take();
        using (Assert.EnterMultipleScope())
        {
            Assert.That(Exists(shownWindow), Is.False);
            Assert.That(seen[0].Opacity, Is.EqualTo(1), "shown at full opacity");
            Assert.That(seen.Skip(1).Select(f => f.Opacity), Is.Not.Empty.And.All.LessThan(1), "faded before it went");
            Assert.That(seen.Skip(1).Select(f => f.Opacity), Is.Ordered.Descending);
            Assert.That(seen[0].Measure, Is.GreaterThan(0), "the text shows over the screen");
            // as far as the fade got before it went: a stalled runner may skip its last frames
            Assert.That(seen[^1].Measure, Is.LessThan(seen[0].Measure * (seen[^1].Opacity + 0.1)), "the text fades into the screen");
        }
    }

    // a notice that replaces one still showing fades into the screen as it was under both, not into what the first one
    // left. the screen is the root window's own pixels, painted the colour once; the second notice copies it while the
    // first still covers it, before the server repaints the root's default background where the first one was: only
    // the first notice's copy of the screen has the plain colour there
    [Test]
    public async Task WithoutACompositor_AReplacingNotice_FadesIntoTheScreen_NotIntoTheOneItReplaced()
    {
        const uint background = 0x3A6EA5;
        PaintRootUnrepainted(background);

        using var osd = new XorgOsdNotification(NullLogger<XorgOsdNotification>.Instance, Held, FadeFor);
        var frames = Record(osd, frame => DistanceFrom(frame.Under, background, frame.Window));

        osd.Show("Relative mouse: On");
        var first = await frames.WaitFor(1);
        osd.Show("Off");
        var second = await frames.WaitFor(2);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(second.Window, Is.Not.EqualTo(first.Window));
            Assert.That(Exists(first.Window), Is.False, "the first is gone");
            Assert.That(first.Measure, Is.Zero, "the first copied the plain screen");
            Assert.That(second.Measure, Is.Zero, "the second's copy has no trace of the first");
        }
    }

    [Test]
    public async Task WithACompositor_ItIsAnArgbWindow_ClickThrough_AtFullOpacity()
    {
        ClaimCompositorSelection();
        using var osd = new XorgOsdNotification(NullLogger<XorgOsdNotification>.Instance, Held, FadeFor);
        var (shown, _) = Watch(osd);

        osd.Show("Pasted!");
        var window = await shown.Task.WaitAsync(TestBound.Bound);

        using (Assert.EnterMultipleScope())
        {
            var attributes = Attributes(window);
            Assert.That(attributes.MapState, Is.EqualTo(NativeMethods.IsViewable));
            Assert.That(attributes.Depth, Is.EqualTo(32));
            Assert.That(OnScreen(attributes), Is.True);
            Assert.That(ShapeRectangles(window, NativeMethods.ShapeInput), Is.Zero, "the pointer clicks through");
            Assert.That(OpacityOf(window), Is.EqualTo(uint.MaxValue));
        }
    }

    [Test]
    public async Task WithACompositor_ItFadesOutBeforeItGoes()
    {
        ClaimCompositorSelection();
        using var osd = new XorgOsdNotification(NullLogger<XorgOsdNotification>.Instance, ShowFor, FadeFor);
        var (shown, hidden) = Watch(osd);
        var frames = Record(osd, _ => 0);

        osd.Show("Pasted!");
        var window = await shown.Task.WaitAsync(TestBound.Bound);
        Assert.That(await hidden.Task.WaitAsync(TestBound.Bound), Is.EqualTo(window));

        var opacities = frames.Take().Select(f => f.Opacity).ToList();
        using (Assert.EnterMultipleScope())
        {
            Assert.That(Exists(window), Is.False);
            Assert.That(opacities[0], Is.EqualTo(1), "shown at full opacity");
            Assert.That(opacities.Skip(1), Is.Not.Empty.And.All.LessThan(1), "faded before it went");
            Assert.That(opacities.Skip(1), Is.Ordered.Descending);
        }
    }

    // a notice too long to draw whole is cut, and the OSD still shows the next one
    [Test]
    public async Task AHugeNotice_IsCut_AndTheOsdCarriesOn()
    {
        using var osd = new XorgOsdNotification(NullLogger<XorgOsdNotification>.Instance, Held, FadeFor);
        var windows = new List<nint>();
        var second = new TaskCompletionSource<nint>(TaskCreationOptions.RunContinuationsAsynchronously);
        osd.Shown += w =>
        {
            lock (windows)
            {
                windows.Add(w);
                if (windows.Count == 2) second.TrySetResult(w);
            }
        };

        osd.Show(new string('W', 100_000));
        var first = await WaitForFirst(windows);
        Assert.That(Attributes(first).Width, Is.LessThan(short.MaxValue));
        osd.Show("Locking slaves");

        Assert.That(Attributes(await second.Task.WaitAsync(TestBound.Bound)).MapState, Is.EqualTo(NativeMethods.IsViewable));
    }

    // a second message replaces the first at once
    [Test]
    public async Task ANewMessage_ReplacesTheOneShowing()
    {
        using var osd = new XorgOsdNotification(NullLogger<XorgOsdNotification>.Instance, Held, FadeFor);
        var shownWindows = new List<nint>();
        var second = new TaskCompletionSource<nint>(TaskCreationOptions.RunContinuationsAsynchronously);
        osd.Shown += w =>
        {
            lock (shownWindows)
            {
                shownWindows.Add(w);
                if (shownWindows.Count == 2) second.TrySetResult(w);
            }
        };

        osd.Show("Relative mouse: On");
        await WaitForFirst(shownWindows);
        osd.Show("Relative mouse: Off");
        var last = await second.Task.WaitAsync(TestBound.Bound);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(Exists(shownWindows[0]), Is.False, "the first one is gone");
            Assert.That(Attributes(last).MapState, Is.EqualTo(NativeMethods.IsViewable));
        }
    }

    [Test]
    public void ShowAndDispose_AfterDispose_AreIgnored()
    {
        var osd = new XorgOsdNotification(NullLogger<XorgOsdNotification>.Instance, ShowFor, FadeFor);
        osd.Dispose();
        using (Assert.EnterMultipleScope())
        {
            Assert.DoesNotThrow(() => osd.Show("Locking slaves"));
            Assert.DoesNotThrow(osd.Dispose);
        }
    }

    private static async Task<nint> WaitForFirst(List<nint> windows)
    {
        using var timeout = new CancellationTokenSource(TestBound.Bound);
        while (true)
        {
            lock (windows)
                if (windows.Count > 0) return windows[0];
            await Task.Delay(10, timeout.Token);
        }
    }

    private static (TaskCompletionSource<nint> Shown, TaskCompletionSource<nint> Hidden) Watch(XorgOsdNotification osd)
    {
        var shown = new TaskCompletionSource<nint>(TaskCreationOptions.RunContinuationsAsynchronously);
        var hidden = new TaskCompletionSource<nint>(TaskCreationOptions.RunContinuationsAsynchronously);
        osd.Shown += w => shown.TrySetResult(w);
        osd.Hidden += w => hidden.TrySetResult(w);
        return (shown, hidden);
    }

    // stands in for a compositor: owns the selection compositors own while they run
    private void ClaimCompositorSelection()
    {
        var root = NativeMethods.XDefaultRootWindow(_display);
        _compositor = NativeMethods.XCreateSimpleWindow(_display, root, 0, 0, 1, 1, 0, 0, 0);
        var selection = NativeMethods.XInternAtom(_display, "_NET_WM_CM_S0", false);
        _ = NativeMethods.XSetSelectionOwner(_display, selection, _compositor, 0);
        _ = NativeMethods.XSync(_display, false);
        Assert.That(NativeMethods.XGetSelectionOwner(_display, selection), Is.EqualTo(_compositor));
    }

    private XWindowAttributes Attributes(nint window)
    {
        XWindowAttributes attributes = default;
        Assert.That(XErrors.Succeeds(_display, () => _ = NativeMethods.XGetWindowAttributes(_display, window, out attributes)), Is.True,
            "the window exists");
        return attributes;
    }

    // a full-screen window of the colour, which the server repaints when what covered it goes
    private void MapBackdrop(uint colour)
    {
        var root = NativeMethods.XDefaultRootWindow(_display);
        var screen = NativeMethods.XDefaultScreen(_display);
        _backdrop = NativeMethods.XCreateSimpleWindow(_display, root, 0, 0, (uint)NativeMethods.XDisplayWidth(_display, screen),
            (uint)NativeMethods.XDisplayHeight(_display, screen), 0, 0, (nint)colour);
        _ = NativeMethods.XMapWindow(_display, _backdrop);
        _ = NativeMethods.XSync(_display, false);
    }

    // the root window's pixels painted the colour once, over its default background (a None background on the root
    // is the default one); the server repaints that default only where an expose asks, and XClearWindow in TearDown
    // puts the whole of it back
    private void PaintRootUnrepainted(uint colour)
    {
        var root = NativeMethods.XDefaultRootWindow(_display);
        var screen = NativeMethods.XDefaultScreen(_display);
        _ = NativeMethods.XSetWindowBackgroundPixmap(_display, root, nint.Zero);
        var gc = NativeMethods.XCreateGC(_display, root, 0, nint.Zero);
        _ = NativeMethods.XSetForeground(_display, gc, colour);
        _ = NativeMethods.XFillRectangle(_display, root, gc, 0, 0, (uint)NativeMethods.XDisplayWidth(_display, screen),
            (uint)NativeMethods.XDisplayHeight(_display, screen));
        _ = NativeMethods.XFreeGC(_display, gc);
        _ = NativeMethods.XSync(_display, false);
        _rootPainted = true;
    }

    // how far what the screen shows where the window is lies from the colour: read from the root, so outside the
    // window's shape it is what is under it, as the user sees it
    private double OnScreenDistanceFrom(nint window, uint colour)
    {
        var at = Attributes(window);
        return DistanceFrom(NativeMethods.XDefaultRootWindow(_display), colour, at.X, at.Y, at.Width, at.Height);
    }

    // how far the drawable's pixels are from the colour, summed over red, green and blue (24-bit TrueColor, 0xRRGGBB,
    // as the lane's Xvfb runs), over the window's size
    private double DistanceFrom(nint drawable, uint colour, nint sizeOf)
    {
        var size = Attributes(sizeOf);
        return DistanceFrom(drawable, colour, 0, 0, size.Width, size.Height);
    }

    private double DistanceFrom(nint drawable, uint colour, int x, int y, int width, int height)
    {
        var image = NativeMethods.XGetImage(_display, drawable, x, y, (uint)width, (uint)height, nuint.MaxValue, NativeMethods.ZPixmap);
        if (image == nint.Zero) throw new InvalidOperationException("XGetImage failed");
        try
        {
            double sum = 0;
            for (var row = 0; row < height; row++)
                for (var column = 0; column < width; column++)
                {
                    var pixel = (uint)NativeMethods.XGetPixel(image, column, row);
                    for (var shift = 0; shift < 24; shift += 8)
                        sum += Math.Abs((int)((pixel >> shift) & 0xFF) - (int)((colour >> shift) & 0xFF));
                }
            return sum;
        }
        finally
        {
            _ = NativeMethods.XDestroyImage(image);
        }
    }

    // each frame the OSD shows, measured on the OSD thread while the frame is current; a measurement that throws is
    // kept and thrown on the test's thread, where the OSD's own handling cannot swallow it
    private Frames Record(XorgOsdNotification osd, Func<OsdFrame, double> measure)
    {
        var frames = new Frames();
        osd.Frame += frame =>
        {
            try
            {
                _ = NativeMethods.XSync(_display, false);
                frames.Add(new MeasuredFrame(frame.Window, frame.Opacity, measure(frame)));
            }
            catch (Exception ex)
            {
                frames.Fail(ex);
            }
        };
        return frames;
    }

    private sealed record MeasuredFrame(nint Window, double Opacity, double Measure);

    private sealed class Frames
    {
        private readonly List<MeasuredFrame> _frames = [];
        private Exception? _failure;
        private readonly SemaphoreSlim _added = new(0);

        internal void Add(MeasuredFrame frame)
        {
            lock (_frames) _frames.Add(frame);
            _added.Release();
        }

        internal void Fail(Exception ex)
        {
            lock (_frames) _failure ??= ex;
            _added.Release();
        }

        internal List<MeasuredFrame> Take()
        {
            lock (_frames)
            {
                if (_failure != null) throw new AssertionException("measuring a frame failed", _failure);
                return [.. _frames];
            }
        }

        // the count'th frame (from 1), once shown
        internal async Task<MeasuredFrame> WaitFor(int count)
        {
            using var timeout = new CancellationTokenSource(TestBound.Bound);
            while (true)
            {
                var frames = Take();
                if (frames.Count >= count) return frames[count - 1];
                await _added.WaitAsync(timeout.Token);
            }
        }
    }

    private bool Exists(nint window) =>
        XErrors.Succeeds(_display, () => _ = NativeMethods.XGetWindowAttributes(_display, window, out _));

    private bool OnScreen(XWindowAttributes attributes)
    {
        var screen = NativeMethods.XDefaultScreen(_display);
        return attributes is { X: >= 0, Y: >= 0, Width: > 0, Height: > 0 }
               && attributes.X + attributes.Width <= NativeMethods.XDisplayWidth(_display, screen)
               && attributes.Y + attributes.Height <= NativeMethods.XDisplayHeight(_display, screen);
    }

    private int ShapeRectangles(nint window, int kind)
    {
        var region = NativeMethods.XFixesCreateRegionFromWindow(_display, window, kind);
        try
        {
            var rectangles = NativeMethods.XFixesFetchRegion(_display, region, out var count);
            if (rectangles != nint.Zero) _ = NativeMethods.XFree(rectangles);
            return count;
        }
        finally
        {
            NativeMethods.XFixesDestroyRegion(_display, region);
        }
    }

    private uint? OpacityOf(nint window)
    {
        var property = NativeMethods.XInternAtom(_display, "_NET_WM_WINDOW_OPACITY", false);
        uint? value = null;
        _ = XErrors.Succeeds(_display, () =>
        {
            if (NativeMethods.XGetWindowProperty(_display, window, property, 0, 1, false, NativeMethods.XA_CARDINAL,
                    out _, out var format, out var items, out _, out var data) != 0 || data == nint.Zero) return;
            if (format == 32 && items == 1) value = (uint)System.Runtime.InteropServices.Marshal.ReadInt64(data);
            _ = NativeMethods.XFree(data);
        });
        return value;
    }
}

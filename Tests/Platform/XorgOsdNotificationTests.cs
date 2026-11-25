using Hydra.Platform.Linux;
using Microsoft.Extensions.Logging.Abstractions;
using Tests.Setup;

namespace Tests.Platform;

// the X11 OSD against a live X server: a window over the screen, clicked through, gone after its time; faded where a
// compositor owns _NET_WM_CM_S0, shaped to its text where none does
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

    [OneTimeSetUp]
    public void RequireX11() => X11Required.OrIgnore();

    [SetUp]
    public void OpenDisplay() => _display = XlibRuntime.OpenDisplayOrThrow("OSD tests");

    [TearDown]
    public void CloseDisplay()
    {
        if (_compositor != nint.Zero) _ = NativeMethods.XDestroyWindow(_display, _compositor);
        _compositor = nint.Zero;
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
            Assert.That(OpacityOf(window), Is.Null);
        }
    }

    [Test]
    public async Task WithoutACompositor_ItGoesAfterItsTime_WithNoFade()
    {
        using var osd = new XorgOsdNotification(NullLogger<XorgOsdNotification>.Instance, ShowFor, FadeFor);
        var (shown, hidden) = Watch(osd);
        var opacities = 0;
        osd.Opacity += (_, _) => Interlocked.Increment(ref opacities);

        osd.Show("Cursor lock: Off");
        var window = await shown.Task.WaitAsync(TestBound.Bound);

        Assert.That(await hidden.Task.WaitAsync(TestBound.Bound), Is.EqualTo(window));
        using (Assert.EnterMultipleScope())
        {
            Assert.That(Exists(window), Is.False);
            Assert.That(opacities, Is.Zero);
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
        var opacities = new List<uint>();
        osd.Opacity += (_, value) => { lock (opacities) opacities.Add(value); };

        osd.Show("Pasted!");
        var window = await shown.Task.WaitAsync(TestBound.Bound);
        Assert.That(await hidden.Task.WaitAsync(TestBound.Bound), Is.EqualTo(window));

        lock (opacities)
        {
            using (Assert.EnterMultipleScope())
            {
                Assert.That(Exists(window), Is.False);
                Assert.That(opacities[0], Is.EqualTo(uint.MaxValue), "shown at full opacity");
                Assert.That(opacities.Skip(1), Is.Not.Empty.And.All.LessThan(uint.MaxValue), "faded before it went");
                Assert.That(opacities.Skip(1), Is.Ordered.Descending);
            }
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

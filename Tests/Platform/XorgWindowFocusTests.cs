using Hydra.Platform.Linux;
using Hydra.Screen;
using Microsoft.Extensions.Logging.Abstractions;
using Tests.Setup;

namespace Tests.Platform;

// the real Xlib focus code against Xvfb, which runs no window manager: the root's own stacking, then one the test
// stands in for, keeping _NET_CLIENT_LIST_STACKING and taking the activation request
[TestFixture]
[Category("Linux")]
[NonParallelizable]
public class XorgWindowFocusTests
{
    // two side-by-side "screens" of the root, far enough apart that each holds one test window
    private static readonly ScreenBounds LeftHalf = new(0, 0, 600, 600);
    private static readonly ScreenBounds RightHalf = new(600, 0, 600, 600);

    private nint _display;
    private nint _root;
    private readonly List<nint> _windows = [];

    [OneTimeSetUp]
    public void RequireX11() => X11Required.OrIgnore();

    [SetUp]
    public void OpenDisplay()
    {
        _display = XlibRuntime.OpenDisplayOrThrow("window focus tests");
        _root = NativeMethods.XDefaultRootWindow(_display);
    }

    [TearDown]
    public void CloseDisplay()
    {
        _ = NativeMethods.XDeleteProperty(_display, _root, Atom("_NET_CLIENT_LIST_STACKING"));
        foreach (var window in _windows) _ = NativeMethods.XDestroyWindow(_display, window);
        _windows.Clear();
        _ = NativeMethods.XCloseDisplay(_display);
    }

    [Test]
    public void WithNoWindowManager_TheFrontmostOnTheScreenGetsTheFocus()
    {
        var left = Window(100, 100);
        var right = Window(800, 100);
        using var focus = new XorgWindowFocus(NullLogger<XorgWindowFocus>.Instance);

        focus.Focus(LeftHalf);
        var afterLeft = Focused();
        focus.Focus(RightHalf);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(afterLeft, Is.EqualTo(left));
            Assert.That(Focused(), Is.EqualTo(right));
        }
    }

    // two on one screen: the one stacked on top is the one last worked in
    [Test]
    public void TheTopOfTwoOnOneScreen_GetsTheFocus()
    {
        var below = Window(100, 100);
        _ = Window(150, 150);
        _ = NativeMethods.XRaiseWindow(_display, below);
        _ = NativeMethods.XSync(_display, false);
        using var focus = new XorgWindowFocus(NullLogger<XorgWindowFocus>.Instance);

        focus.Focus(LeftHalf);

        Assert.That(Focused(), Is.EqualTo(below));
    }

    // a window manager's stacking wins over the root's, and the window is activated through it, not focused directly
    [Test]
    public void WithAWindowManager_ItsStackingDecides_AndItIsAskedToActivate()
    {
        var first = Window(100, 100);
        var second = Window(150, 150);
        // the manager's list is bottom to top: first above second, the reverse of the root's order
        _ = NativeMethods.XChangeProperty(_display, _root, Atom("_NET_CLIENT_LIST_STACKING"), NativeMethods.XA_WINDOW, 32, PropModeReplace, [second, first], 2);
        _ = NativeMethods.XSelectInput(_display, _root, NativeMethods.SubstructureRedirectMask);
        _ = NativeMethods.XSync(_display, false);
        using var focus = new XorgWindowFocus(NullLogger<XorgWindowFocus>.Instance);

        focus.Focus(LeftHalf);
        var request = NextClientMessage();

        using (Assert.EnterMultipleScope())
        {
            Assert.That(request.ClientMessageType, Is.EqualTo(Atom("_NET_ACTIVE_WINDOW")));
            Assert.That(request.EventWindow, Is.EqualTo(first));
            Assert.That(request.ClientMessageData0, Is.EqualTo((nint)2), "as a pager asks, so no focus-stealing prevention applies");
        }
    }

    // a panel or desktop is stacked above the windows a user works in; minimised ones are listed too
    [TestCase("_NET_WM_WINDOW_TYPE", "_NET_WM_WINDOW_TYPE_DOCK")]
    [TestCase("_NET_WM_WINDOW_TYPE", "_NET_WM_WINDOW_TYPE_DESKTOP")]
    [TestCase("_NET_WM_STATE", "_NET_WM_STATE_HIDDEN")]
    public void ADockDesktopOrHiddenWindowOnTop_IsPassedOver(string property, string value)
    {
        var worked = Window(100, 100);
        var above = Window(150, 150);
        _ = NativeMethods.XChangeProperty(_display, above, Atom(property), NativeMethods.XA_ATOM, 32, PropModeReplace, [Atom(value)], 1);
        _ = NativeMethods.XSync(_display, false);
        using var focus = new XorgWindowFocus(NullLogger<XorgWindowFocus>.Instance);

        focus.Focus(LeftHalf);

        Assert.That(Focused(), Is.EqualTo(worked));
    }

    [Test]
    public void ANormalOrDialogTypedWindow_Counts()
    {
        _ = Window(100, 100);
        var dialog = Window(150, 150);
        _ = NativeMethods.XChangeProperty(_display, dialog, Atom("_NET_WM_WINDOW_TYPE"), NativeMethods.XA_ATOM, 32, PropModeReplace,
            [Atom("_NET_WM_WINDOW_TYPE_DIALOG"), Atom("_NET_WM_WINDOW_TYPE_NORMAL")], 2);
        _ = NativeMethods.XSync(_display, false);
        using var focus = new XorgWindowFocus(NullLogger<XorgWindowFocus>.Instance);

        focus.Focus(LeftHalf);

        Assert.That(Focused(), Is.EqualTo(dialog));
    }

    [Test]
    public void NoWindowOnTheScreen_ChangesNothing()
    {
        var only = Window(100, 100);
        _ = NativeMethods.XSetInputFocus(_display, only, NativeMethods.RevertToParent, 0);
        _ = NativeMethods.XSync(_display, false);
        using var focus = new XorgWindowFocus(NullLogger<XorgWindowFocus>.Instance);

        focus.Focus(RightHalf);

        Assert.That(Focused(), Is.EqualTo(only));
    }

    private const int PropModeReplace = 0;

    // a mapped 200x200 window at (x, y), on top of the root's stack
    private nint Window(int x, int y)
    {
        var window = NativeMethods.XCreateSimpleWindow(_display, _root, x, y, 200, 200, 0, 0, 0);
        _windows.Add(window);
        _ = NativeMethods.XMapWindow(_display, window);
        _ = NativeMethods.XSync(_display, false);
        return window;
    }

    private nint Focused()
    {
        _ = NativeMethods.XSync(_display, false);
        _ = NativeMethods.XGetInputFocus(_display, out var focused, out _);
        return focused;
    }

    private nint Atom(string name) => NativeMethods.XInternAtom(_display, name, false);

    // the first ClientMessage redirected to us as the stand-in window manager; XSync makes sure it has arrived
    private XEvent NextClientMessage()
    {
        _ = NativeMethods.XSync(_display, false);
        while (NativeMethods.XPending(_display) > 0)
        {
            _ = NativeMethods.XNextEvent(_display, out var ev);
            if (ev.Type == NativeMethods.ClientMessage) return ev;
        }
        throw new AssertionException("no activation request reached the window manager");
    }
}

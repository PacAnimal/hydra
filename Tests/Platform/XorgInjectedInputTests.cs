using Hydra.Keyboard;
using Hydra.Mouse;
using Hydra.Platform.Linux;
using Hydra.Screen;
using Microsoft.Extensions.Logging.Abstractions;
using Tests.Setup;

namespace Tests.Platform;

// An Xorg master tells pointer input another client injects into its display (XTest, as a VNC server or xdotool does)
// from the user's own: during a visit it is not forwarded but raised as InjectedInput, with where the pointer now is,
// and at home its motion is not reported, so it crosses no edge. Injected keys stay the user's own. The test's own
// input is xtest's, so here it stands for the VNC server.
[TestFixture]
[Category("Linux")]
[NonParallelizable]
public class XorgInjectedInputTests
{
    private nint _display;
    private uint _keycodeA;

    [OneTimeSetUp]
    public void RequireX11() => X11Required.OrIgnore();

    [SetUp]
    public void OpenDisplay()
    {
        _display = XlibRuntime.OpenDisplay();
        Assert.That(_display, Is.Not.EqualTo(nint.Zero), "pre-condition: an X server");
        _keycodeA = NativeMethods.XKeysymToKeycode(_display, 0x61);
        Assert.That(_keycodeA, Is.Not.Zero, "pre-condition: the keymap has 'a'");
    }

    [TearDown]
    public void CloseDisplay()
    {
        if (_display != nint.Zero) _ = NativeMethods.XCloseDisplay(_display);
    }

    // a handler started with the given setting, which records what it reports
    private sealed class Tap : IAsyncDisposable
    {
        public XorgInputHandler Handler { get; }
        public TaskCompletionSource<ScreenPoint?> Injected { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Delta { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Moved { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Button { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource<KeyEvent> Key { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public List<MouseButtonEvent> Buttons { get; } = [];
        public int InjectedCount;

        private Tap(XorgInputHandler handler) => Handler = handler;

        public static async Task<Tap> Start(bool injectedInputReturnsHome)
        {
            var tap = new Tap(new XorgInputHandler(NullLogger<XorgInputHandler>.Instance,
                TransitionTestHelper.ProfileWith(injectedInputReturnsHome: injectedInputReturnsHome)));
            tap.Handler.InjectedInput += at =>
            {
                Interlocked.Increment(ref tap.InjectedCount);
                tap.Injected.TrySetResult(at);
            };
            await tap.Handler.StartEventTap((_, _) => tap.Moved.TrySetResult(), (_, _) => tap.Delta.TrySetResult(), k => tap.Key.TrySetResult(k),
                b =>
                {
                    lock (tap.Buttons) tap.Buttons.Add(b);
                    tap.Button.TrySetResult();
                }, _ => { });
            return tap;
        }

        public async ValueTask DisposeAsync()
        {
            Handler.IsOnVirtualScreen = false;
            await Handler.DisposeAsync();
        }
    }

    [Test]
    public async Task InjectedMotion_DuringAVisit_IsRaisedWithWhereItPointed_AndNotForwarded()
    {
        await using var tap = await Tap.Start(injectedInputReturnsHome: true);
        tap.Handler.IsOnVirtualScreen = true;

        Fake(() => NativeMethods.XTestFakeMotionEvent(_display, -1, 123, 77, 0));

        var at = await tap.Injected.Task.WaitAsync(TestBound.Bound);
        using (Assert.EnterMultipleScope())
        {
            Assert.That(at, Is.EqualTo(new ScreenPoint(123, 77)));
            Assert.That(tap.Delta.Task.IsCompleted, Is.False, "no delta forwarded");
        }
    }

    [Test]
    public async Task InjectedButton_DuringAVisit_IsRaised_AndNotForwarded()
    {
        await using var tap = await Tap.Start(injectedInputReturnsHome: true);
        tap.Handler.IsOnVirtualScreen = true;

        Fake(() => NativeMethods.XTestFakeButtonEvent(_display, 1, true, 0));
        Fake(() => NativeMethods.XTestFakeButtonEvent(_display, 1, false, 0));

        await tap.Injected.Task.WaitAsync(TestBound.Bound);
        // the release follows the press through the same queue; a move after it marks that both were handled
        Fake(() => NativeMethods.XTestFakeRelativeMotionEvent(_display, 5, 5, 0));
        await WaitFor(() => Volatile.Read(ref tap.InjectedCount) >= 2);
        lock (tap.Buttons)
            Assert.That(tap.Buttons, Is.Empty, "neither the press nor the release forwarded");
    }

    [Test]
    public async Task InjectedKey_DuringAVisit_IsForwardedAsTheUsersOwn()
    {
        // xcape, espanso and keep-awake scripts inject keys while the user works on the remote
        await using var tap = await Tap.Start(injectedInputReturnsHome: true);
        tap.Handler.IsOnVirtualScreen = true;

        try
        {
            Fake(() => NativeMethods.XTestFakeKeyEvent(_display, _keycodeA, true, 0));
        }
        finally
        {
            Fake(() => NativeMethods.XTestFakeKeyEvent(_display, _keycodeA, false, 0));
        }

        var key = await tap.Key.Task.WaitAsync(TestBound.Bound);
        using (Assert.EnterMultipleScope())
        {
            Assert.That(key.Character, Is.EqualTo('a'));
            Assert.That(tap.InjectedCount, Is.Zero);
        }
    }

    [Test]
    public async Task InjectedMotion_WithTheSettingOff_IsForwardedAsTheUsersOwn()
    {
        await using var tap = await Tap.Start(injectedInputReturnsHome: false);
        tap.Handler.IsOnVirtualScreen = true;

        Fake(() => NativeMethods.XTestFakeRelativeMotionEvent(_display, 10, 0, 0));

        await tap.Delta.Task.WaitAsync(TestBound.Bound);
        Assert.That(tap.InjectedCount, Is.Zero);
    }

    [Test]
    public async Task InjectedMotion_AtHome_IsNotReported_SoItCrossesNoEdge()
    {
        await using var tap = await Tap.Start(injectedInputReturnsHome: true);

        Fake(() => NativeMethods.XTestFakeRelativeMotionEvent(_display, 7, 3, 0));
        // an injected click at home is the user's own: it marks that the move before it was handled
        Fake(() => NativeMethods.XTestFakeButtonEvent(_display, 1, true, 0));
        Fake(() => NativeMethods.XTestFakeButtonEvent(_display, 1, false, 0));

        await tap.Button.Task.WaitAsync(TestBound.Bound);
        using (Assert.EnterMultipleScope())
        {
            Assert.That(tap.Moved.Task.IsCompleted, Is.False, "the move reached the router, which could cross an edge on it");
            Assert.That(tap.InjectedCount, Is.Zero);
        }
    }

    [Test]
    public async Task InjectedMotion_AtHome_WithTheSettingOff_IsLocalMotion()
    {
        await using var tap = await Tap.Start(injectedInputReturnsHome: false);

        Fake(() => NativeMethods.XTestFakeRelativeMotionEvent(_display, 7, 3, 0));

        await tap.Moved.Task.WaitAsync(TestBound.Bound);
        Assert.That(tap.InjectedCount, Is.Zero);
    }

    private void Fake(Func<int> inject)
    {
        _ = inject();
        _ = NativeMethods.XSync(_display, false);
    }

    private static async Task WaitFor(Func<bool> condition)
    {
        var until = DateTime.UtcNow + TestBound.Bound;
        while (!condition())
        {
            if (DateTime.UtcNow > until) Assert.Fail("timed out");
            await Task.Delay(10);
        }
    }
}

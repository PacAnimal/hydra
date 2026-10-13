using Hydra.Keyboard;
using Hydra.Platform.Linux;
using Microsoft.Extensions.Logging.Abstractions;
using Tests.Setup;

namespace Tests.Keyboard;

// what an Xorg master knows of the keys held when its keyboard grab starts, so their repeats and release stay home
[TestFixture]
[Category("Linux")]
public class XorgHeldKeyTests
{
    private const ulong XkA = 0x61;
    private const ulong XkB = 0x62;

    private nint _display;
    private uint _keycodeA;
    private uint _keycodeB;

    [OneTimeSetUp]
    public void RequireX11() => X11Required.OrIgnore();

    [SetUp]
    public void OpenDisplay()
    {
        _display = XlibRuntime.OpenDisplay();
        Assert.That(_display, Is.Not.EqualTo(nint.Zero), "pre-condition: an X server");
        _keycodeA = NativeMethods.XKeysymToKeycode(_display, XkA);
        _keycodeB = NativeMethods.XKeysymToKeycode(_display, XkB);
        Assert.That(new[] { _keycodeA, _keycodeB }, Is.All.Not.Zero, "pre-condition: the keymap has 'a' and 'b'");
    }

    [TearDown]
    public void CloseDisplay()
    {
        if (_display != nint.Zero) _ = NativeMethods.XCloseDisplay(_display);
    }

    [Test]
    public void HeldKeycodes_ReadsTheKeysDownOnTheServer()
    {
        try
        {
            FakeKey(_keycodeA, isPress: true);
            Assert.That(XorgDisplayHelper.HeldKeycodes(_display), Does.Contain(_keycodeA));
        }
        finally
        {
            // every Linux test shares the one Xvfb
            FakeKey(_keycodeA, isPress: false);
        }
        Assert.That(XorgDisplayHelper.HeldKeycodes(_display), Does.Not.Contain(_keycodeA));
    }

    [Test]
    public void Reset_SeedsHeldKeys_TheirPressRepeatsAndTheirReleaseIsNothing()
    {
        var resolver = new XorgKeyResolver();
        resolver.Reset([_keycodeA]);

        var repeat = resolver.Resolve(NativeMethods.KeyPress, _keycodeA, 0, _display);
        var release = resolver.Resolve(NativeMethods.KeyRelease, _keycodeA, 0, _display);
        var pressedAgain = resolver.Resolve(NativeMethods.KeyPress, _keycodeA, 0, _display);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(repeat!.Select(e => (e!.Character, e.IsRepeat)), Is.EqualTo([('a', true)]));
            Assert.That(release, Is.EqualTo(new KeyEvent?[] { null }));
            Assert.That(pressedAgain!.Select(e => (e!.Character, e.IsRepeat)), Is.EqualTo([('a', false)]), "re-pressed during the visit: a real down");
        }
    }

    [Test]
    public void Reset_ForgetsKeysNoLongerHeld()
    {
        var resolver = new XorgKeyResolver();
        _ = resolver.Resolve(NativeMethods.KeyPress, _keycodeA, 0, _display);

        resolver.Reset([]);

        var press = resolver.Resolve(NativeMethods.KeyPress, _keycodeA, 0, _display);
        Assert.That(press!.Select(e => (e!.Character, e.IsRepeat)), Is.EqualTo([('a', false)]));
    }

    [Test]
    public void ForgetReleasedSeeds_DropsOnlySeedsNoLongerHeld()
    {
        var resolver = new XorgKeyResolver();
        resolver.Reset([_keycodeA, _keycodeB]);

        resolver.ForgetReleasedSeeds([_keycodeB]);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(Press(resolver, _keycodeA).Select(e => (e!.Character, e.IsRepeat)), Is.EqualTo([('a', false)]), "released before the grab: a press is a real down");
            Assert.That(Press(resolver, _keycodeB).Select(e => (e!.Character, e.IsRepeat)), Is.EqualTo([('b', true)]), "still held: still a repeat");
        }
    }

    [Test]
    public void ForgetReleasedSeeds_NeverForgetsARealDownNorAddsAKey()
    {
        var resolver = new XorgKeyResolver();
        resolver.Reset([_keycodeA]);
        _ = resolver.Resolve(NativeMethods.KeyRelease, _keycodeA, 0, _display);
        _ = resolver.Resolve(NativeMethods.KeyPress, _keycodeA, 0, _display);

        resolver.ForgetReleasedSeeds([_keycodeB]);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(resolver.Resolve(NativeMethods.KeyRelease, _keycodeA, 0, _display)!.Select(e => e?.Character), Is.EqualTo(new char?[] { 'a' }),
                "a down seen during the visit keeps its up");
            Assert.That(Press(resolver, _keycodeB).Select(e => (e!.Character, e.IsRepeat)), Is.EqualTo([('b', false)]));
        }
    }

    [Test]
    public void EnableDetectableAutoRepeat_IsOnForThisConnection()
    {
        Assert.That(XorgDisplayHelper.EnableDetectableAutoRepeat(_display), Is.True);
    }

    [Test]
    public async Task KeyHeldWhenTheGrabStarts_StaysHome_WhileAKeyPressedDuringTheVisitIsForwarded()
    {
        var seen = new List<KeyEvent>();
        var heldKeySeen = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var sentinelUp = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var handler = new XorgInputHandler(NullLogger<XorgInputHandler>.Instance);
        await handler.StartEventTap((_, _) => { }, null, e =>
        {
            lock (seen) seen.Add(e);
            if (e.Character == 'a') heldKeySeen.TrySetResult();
            if (e is { Type: KeyEventType.KeyUp, Character: 'b' }) sentinelUp.TrySetResult();
        }, _ => { }, _ => { });

        try
        {
            FakeKey(_keycodeA, isPress: true);
            handler.IsOnVirtualScreen = true;
            // the server's own auto-repeat, once its delay has passed: XKB drops a second fake press of a key already down
            await heldKeySeen.Task.WaitAsync(TimeSpan.FromSeconds(10));
            FakeKey(_keycodeA, isPress: false);
            FakeKey(_keycodeB, isPress: true);
            FakeKey(_keycodeB, isPress: false);

            await sentinelUp.Task.WaitAsync(TimeSpan.FromSeconds(10));
        }
        finally
        {
            FakeKey(_keycodeA, isPress: false);
            FakeKey(_keycodeB, isPress: false);
            handler.IsOnVirtualScreen = false;
        }

        lock (seen)
        {
            using (Assert.EnterMultipleScope())
            {
                Assert.That(seen.Where(e => e.IsRepeat).Select(e => e.Character), Has.Member('a'), "the held key reaches the grab only as repeats");
                Assert.That(seen.Where(e => !e.IsRepeat).Select(e => (e.Type, e.Character)),
                    Is.EqualTo([(KeyEventType.KeyDown, 'b'), (KeyEventType.KeyUp, 'b')]));
            }
        }
    }

    private KeyEvent?[] Press(XorgKeyResolver resolver, uint keycode) => resolver.Resolve(NativeMethods.KeyPress, keycode, 0, _display)!;

    private void FakeKey(uint keycode, bool isPress)
    {
        _ = NativeMethods.XTestFakeKeyEvent(_display, keycode, isPress, 0);
        _ = NativeMethods.XSync(_display, false);
    }
}

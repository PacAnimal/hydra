using Hydra.Config;
using Hydra.Platform.Linux;
using Tests.Setup;

namespace Tests.Platform;

// what an Xorg master grabs to hear its hotkeys from home, and what becomes of a grab another client already holds
[TestFixture]
[Category("Linux")]
[NonParallelizable]
public class XorgHotkeyGrabTests
{
    private static readonly uint ChordMask = NativeMethods.ControlMask | NativeMethods.Mod1Mask | NativeMethods.Mod4Mask;

    private nint _display;

    [OneTimeSetUp]
    public void RequireX11() => X11Required.OrIgnore();

    [SetUp]
    public void OpenDisplay() => _display = XlibRuntime.OpenDisplayOrThrow("hotkey grab tests");

    [TearDown]
    public void CloseDisplay() => _ = NativeMethods.XCloseDisplay(_display);

    [Test]
    public void TheDefaults_GrabTheLockAndTheFourArrows_UnderTheChord()
    {
        var keys = XorgHotkeyGrab.Keys(_display, HotkeyBindings.Build(null));

        using (Assert.EnterMultipleScope())
        {
            Assert.That(keys.Select(k => k.Action), Is.EquivalentTo(
            [
                HotkeyAction.ToggleCursorLock, HotkeyAction.SwitchMonitorUp, HotkeyAction.SwitchMonitorDown,
                HotkeyAction.SwitchMonitorLeft, HotkeyAction.SwitchMonitorRight,
            ]));
            Assert.That(keys.Select(k => k.Mods), Has.All.EqualTo(ChordMask));
            Assert.That(keys.Single(k => k.Action == HotkeyAction.ToggleCursorLock).Keycode, Is.EqualTo(Keycode(0x006C)), "XK_l");
            Assert.That(keys.Single(k => k.Action == HotkeyAction.SwitchMonitorUp).Keycode, Is.EqualTo(Keycode(0xFF52)), "XK_Up");
        }
    }

    // the lock as configured, no longer always Ctrl+Alt+Super+L; a disabled action grabs nothing
    [Test]
    public void ARebindingIsGrabbed_AndADisabledActionIsNot()
    {
        var keys = XorgHotkeyGrab.Keys(_display, HotkeyBindings.Build(new() { ["toggleCursorLock"] = ["Shift+F5"], ["switchMonitorUp"] = [] }));

        using (Assert.EnterMultipleScope())
        {
            Assert.That(keys.Single(k => k.Action == HotkeyAction.ToggleCursorLock), Is.EqualTo(new GrabbedKey(Keycode(0xFFC2), NativeMethods.ShiftMask, HotkeyAction.ToggleCursorLock)));
            Assert.That(keys.Select(k => k.Action), Does.Not.Contain(HotkeyAction.SwitchMonitorUp));
        }
    }

    // X's default error handler ends the process on the BadAccess a taken grab answers; Hydra's reports it instead
    [Test]
    public void AGrabAnotherClientHolds_FailsWithoutEndingTheProcess()
    {
        var other = XlibRuntime.OpenDisplayOrThrow("the other client");
        try
        {
            var root = NativeMethods.XDefaultRootWindow(_display);
            var keycode = Keycode(0xFFC6); // XK_F9
            var first = XErrors.Succeeds(other, () => _ = NativeMethods.XGrabKey(other, keycode, ChordMask, root, true, NativeMethods.GrabModeAsync, NativeMethods.GrabModeAsync));
            var second = XErrors.Succeeds(_display, () => _ = NativeMethods.XGrabKey(_display, keycode, ChordMask, root, true, NativeMethods.GrabModeAsync, NativeMethods.GrabModeAsync));

            using (Assert.EnterMultipleScope())
            {
                Assert.That(first, Is.True);
                Assert.That(second, Is.False);
            }
        }
        finally
        {
            _ = NativeMethods.XCloseDisplay(other);
        }
    }

    private int Keycode(ulong keysym) => (int)NativeMethods.XKeysymToKeycode(_display, keysym);
}

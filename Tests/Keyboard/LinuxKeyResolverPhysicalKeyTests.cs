using Hydra.Keyboard;
using Hydra.Platform.Linux;
using Tests.Setup;

namespace Tests.Keyboard;

// PhysicalKey on the two Linux resolvers, which need libX11 with a live X server and libxkbcommon. They self-skip
// elsewhere and run under Xvfb in the test-linux CI job and run-tests.sh linux.
[TestFixture]
[Category("Linux")]
public class LinuxKeyResolverPhysicalKeyTests
{
    [OneTimeSetUp]
    public void RequireX11() => X11Required.OrIgnore();

    [Test]
    public void Xorg_DownRepeatAndUp_CarryTheKeycode()
    {
        var display = NativeMethods.XOpenDisplay(null);
        Assert.That(display, Is.Not.EqualTo(nint.Zero), "pre-condition: an X server");
        try
        {
            var resolver = new XorgKeyResolver();
            const ulong xkA = 0x61;
            var keycode = NativeMethods.XKeysymToKeycode(display, xkA);
            Assert.That(keycode, Is.Not.Zero, "pre-condition: the keymap has 'a'");

            KeyEvent?[] events =
            [
                .. resolver.Resolve(NativeMethods.KeyPress, keycode, 0, display)!,
                .. resolver.Resolve(NativeMethods.KeyPress, keycode, 0, display)!,
                .. resolver.Resolve(NativeMethods.KeyRelease, keycode, 0, display)!,
            ];

            Assert.That(events.Select(e => e!.PhysicalKey), Is.EqualTo([(int)keycode, (int)keycode, (int)keycode]));
        }
        finally
        {
            _ = NativeMethods.XCloseDisplay(display);
        }
    }

    [TestCase(0xFF9FUL /* KP_Delete: the keypad's decimal key */, true)]
    [TestCase(0x2EUL /* period, on the main row */, false)]
    [TestCase(0xFFFFUL /* Delete, the dedicated key */, false)]
    public void Xorg_OnlyTheKeypadsDecimalKey_IsStampedAsIt(ulong keysym, bool keypadDecimal)
    {
        var display = NativeMethods.XOpenDisplay(null);
        Assert.That(display, Is.Not.EqualTo(nint.Zero), "pre-condition: an X server");
        try
        {
            var resolver = new XorgKeyResolver();
            var keycode = NativeMethods.XKeysymToKeycode(display, keysym);
            Assert.That(keycode, Is.Not.Zero, "pre-condition: the keymap has the key");

            KeyEvent?[] events =
            [
                .. resolver.Resolve(NativeMethods.KeyPress, keycode, NativeMethods.Mod2Mask, display)!,
                .. resolver.Resolve(NativeMethods.KeyRelease, keycode, NativeMethods.Mod2Mask, display)!,
            ];

            Assert.That(events.Select(e => e!.IsKeypadDecimal), Is.EqualTo([keypadDecimal, keypadDecimal]));
        }
        finally
        {
            _ = NativeMethods.XCloseDisplay(display);
        }
    }

    [Test]
    public void Evdev_DownRepeatAndUp_CarryTheEvdevCode()
    {
        const uint keyA = 30;
        using var resolver = new EvdevKeyResolver(new LinuxInputConfig.XkbNames("us", "pc105", null, null));

        KeyEvent?[] events = [.. resolver.Resolve(keyA, 1)!, .. resolver.Resolve(keyA, 2)!, .. resolver.Resolve(keyA, 0)!];

        Assert.That(events.Select(e => (e!.Character, e.PhysicalKey)), Is.EqualTo([('a', (int)keyA), ('a', (int)keyA), ('a', (int)keyA)]));
    }

    [Test]
    public void Evdev_OnlyTheKeypadsDecimalKey_IsStampedAsIt()
    {
        const uint keyKpDot = 83, keyKpComma = 121, keyDot = 52;
        using var resolver = new EvdevKeyResolver(new LinuxInputConfig.XkbNames("us", "pc105", null, null));

        KeyEvent?[] keypad = [.. resolver.Resolve(keyKpDot, 1)!, .. resolver.Resolve(keyKpDot, 0)!];
        KeyEvent?[] keypadComma = [.. resolver.Resolve(keyKpComma, 1)!, .. resolver.Resolve(keyKpComma, 0)!];
        KeyEvent?[] mainRow = [.. resolver.Resolve(keyDot, 1)!, .. resolver.Resolve(keyDot, 0)!];

        using (Assert.EnterMultipleScope())
        {
            Assert.That(keypad.Select(e => e!.IsKeypadDecimal), Is.EqualTo([true, true]));
            Assert.That(keypadComma.Select(e => e!.IsKeypadDecimal), Is.EqualTo([true, true]));
            Assert.That(mainRow.Select(e => e!.IsKeypadDecimal), Is.EqualTo([false, false]));
        }
    }
}

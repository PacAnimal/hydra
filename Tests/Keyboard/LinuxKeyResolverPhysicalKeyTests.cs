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

    [Test]
    public void Evdev_DownRepeatAndUp_CarryTheEvdevCode()
    {
        const uint keyA = 30;
        using var resolver = new EvdevKeyResolver(new LinuxInputConfig.XkbNames("us", "pc105", null, null));

        KeyEvent?[] events = [.. resolver.Resolve(keyA, 1)!, .. resolver.Resolve(keyA, 2)!, .. resolver.Resolve(keyA, 0)!];

        Assert.That(events.Select(e => (e!.Character, e.PhysicalKey)), Is.EqualTo([('a', (int)keyA), ('a', (int)keyA), ('a', (int)keyA)]));
    }
}

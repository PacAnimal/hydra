using Hydra.Platform.Linux;

namespace Tests.Keyboard;

// XorgKeyResolver.KeySymToChar: legacy keysyms (0x0100-0x20FF) are not code points, so they go through libxkbcommon or
// the table and a keysym neither knows is no character. the exhaustive table check needs libxkbcommon, so it is
// Category=Linux: it self-skips off Linux and runs in the Linux CI test job and run-tests.sh linux; the rest run everywhere.
[TestFixture]
public class XorgKeySymToCharTests
{
    private const ulong XkUkrainianGheWithUpturnSmall = 0x06AD;
    private const ulong XkUkrainianGheWithUpturnCapital = 0x06BD;
    // a hole in the cyrillic block: keysymdef.h defines nothing there and xkbcommon maps it to nothing
    private const ulong UnassignedCyrillic = 0x06A0;

    [Test]
    public void KeySymToChar_UkrainianGheWithUpturn()
    {
        using (Assert.EnterMultipleScope())
        {
            Assert.That(XorgKeyResolver.KeySymToChar(XkUkrainianGheWithUpturnSmall), Is.EqualTo('ґ'));
            Assert.That(XorgKeyResolver.KeySymToChar(XkUkrainianGheWithUpturnCapital), Is.EqualTo('Ґ'));
        }
    }

    [Test]
    [Category("Linux")]
    public void KeySymToChar_LegacyTable_AgreesWithXkbcommon()
    {
        if (!OperatingSystem.IsLinux())
            Assert.Ignore("Requires Linux with libxkbcommon (run ./run-tests.sh linux)");

        var wrong = Enumerable.Range(0x0100, 0x2100 - 0x0100)
            .Select(k => (Keysym: k, Xkb: EvdevNativeMethods.xkb_keysym_to_utf32((uint)k)))
            .Where(p => p.Xkb is > 0 and <= 0xFFFF && !char.IsSurrogate((char)p.Xkb) && !char.IsControl((char)p.Xkb))
            .Where(p => XorgKeyResolver.LegacyKeySymToChar((ulong)p.Keysym) != (char)p.Xkb)
            .Select(p => $"0x{p.Keysym:X4}: xkbcommon U+{p.Xkb:X4}, table {Describe(XorgKeyResolver.LegacyKeySymToChar((ulong)p.Keysym))}");
        Assert.That(wrong, Is.Empty);
    }

    [Test]
    public void KeySymToChar_UnknownLegacyKeysym_IsNull()
    {
        if (OperatingSystem.IsLinux())
            Assert.That(EvdevNativeMethods.xkb_keysym_to_utf32((uint)UnassignedCyrillic), Is.Zero, "xkbcommon maps it to nothing");
        Assert.That(XorgKeyResolver.KeySymToChar(UnassignedCyrillic), Is.Null);
    }

    // only latin-1, unicode-extension and legacy (0x0100-0x20FF) keysyms are characters: a 3270 key, a dead key
    // (dead_grave, handled as a dead key elsewhere) and the first keysym past the legacy range are none
    [TestCase(0xFD01UL)]
    [TestCase(0xFE50UL)]
    [TestCase(0x2100UL)]
    public void KeySymToChar_NonCharacterKeysym_IsNull(ulong keysym) =>
        Assert.That(XorgKeyResolver.KeySymToChar(keysym), Is.Null);

    [Test]
    public void KeySymToChar_FallsBackToTheTable_WithoutXkbcommon()
    {
        using (Assert.EnterMultipleScope())
        {
            Assert.That(XorgKeyResolver.KeySymToChar(XkUkrainianGheWithUpturnSmall, useXkb: false), Is.EqualTo('ґ'));
            Assert.That(XorgKeyResolver.KeySymToChar(0x20AC, useXkb: false), Is.EqualTo('€'));
            Assert.That(XorgKeyResolver.KeySymToChar(UnassignedCyrillic, useXkb: false), Is.Null);
        }
    }

    private static string Describe(char? ch) => ch is { } c ? $"U+{(int)c:X4}" : "nothing";
}

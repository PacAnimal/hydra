using Hydra.Platform.Linux;
using Tests.Setup;

namespace Tests.Keyboard;

// shortcuts on an X11 master whose active group is not Latin. Xvfb loads only us, so the letter keys are remapped to
// Cyrillic with XChangeKeyboardMapping (group 0 then has no Latin letters) and restored after; the key under test is
// one left alone that has a printable AltGr level (LSGT's bar on pc105), since a core mapping cannot give a key a level
// its type lacks
[TestFixture]
[Category("Linux")]
public sealed class XorgKeyResolverLayoutTests
{
    private nint _display;
    private nint _resolverDisplay;
    private int _perKeycode;
    private readonly List<(uint Keycode, ulong[] Row)> _saved = [];

    [OneTimeSetUp]
    public void RequireX11() => X11Required.OrIgnore();

    [SetUp]
    public void Open()
    {
        _display = XlibRuntime.OpenDisplay();
        Assert.That(_display, Is.Not.EqualTo(nint.Zero), "pre-condition: an X server");
    }

    [TearDown]
    public void Close()
    {
        if (_resolverDisplay != nint.Zero) _ = NativeMethods.XCloseDisplay(_resolverDisplay);
        _resolverDisplay = nint.Zero;
        if (_display == nint.Zero) return;
        // every Linux test shares the one Xvfb
        foreach (var (keycode, row) in _saved)
            _ = NativeMethods.XChangeKeyboardMapping(_display, (int)keycode, _perKeycode, row, 1);
        _saved.Clear();
        _ = NativeMethods.XSync(_display, false);
        _ = NativeMethods.XCloseDisplay(_display);
        _display = nint.Zero;
    }

    [Test]
    public void NonLatinGroup_CtrlAltGr_KeepsTheAltGrKeysym()
    {
        var (keycode, altGr) = AltGrKey();
        var latinKey = LatinShortcutKeysym.ReferenceBase(keycode);
        Assume.That(latinKey, Is.Not.EqualTo(altGr), "pre-condition: the us reference's key differs from the AltGr level");
        MakeGroup0Cyrillic();
        _resolverDisplay = XlibRuntime.OpenDisplay();
        Assert.That(_resolverDisplay, Is.Not.EqualTo(nint.Zero));

        var resolver = new XorgKeyResolver();
        var altGrShortcut = Press(resolver, keycode, NativeMethods.ControlMask | NativeMethods.Mod5Mask);
        var shortcut = Press(resolver, keycode, NativeMethods.ControlMask);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(altGrShortcut, Is.EqualTo((char)altGr), "the AltGr level, not the Latin key's base");
            Assert.That(shortcut, Is.EqualTo((char)latinKey), "pre-condition: without AltGr, the Latin key");
        }
    }

    private char? Press(XorgKeyResolver resolver, uint keycode, uint state)
    {
        var down = resolver.Resolve(NativeMethods.KeyPress, keycode, state, _resolverDisplay);
        _ = resolver.Resolve(NativeMethods.KeyRelease, keycode, state, _resolverDisplay);
        Assert.That(down, Is.Not.Null.And.Not.Empty, "pre-condition: the key resolves");
        return down![^1]?.Character;
    }

    // a non-letter key whose group-0 level 2 (AltGr) is printable ASCII other than its base
    private (uint Keycode, ulong Keysym) AltGrKey()
    {
        _ = NativeMethods.XDisplayKeycodes(_display, out var min, out var max);
        for (var keycode = (uint)min; keycode <= (uint)max; keycode++)
        {
            var level0 = NativeMethods.XkbKeycodeToKeysym(_display, keycode, 0, 0);
            var level2 = NativeMethods.XkbKeycodeToKeysym(_display, keycode, 0, 2);
            if (level0 is >= 'a' and <= 'z' || level2 is < 0x21 or > 0x7E || level2 == level0) continue;
            return (keycode, level2);
        }
        Assert.Ignore("pre-condition: a key with a printable AltGr level");
        return default;
    }

    // every key typing a-z at group 0's base types a Cyrillic letter there instead, so no group is Latin
    private unsafe void MakeGroup0Cyrillic()
    {
        _ = NativeMethods.XDisplayKeycodes(_display, out var min, out var max);
        var map = NativeMethods.XGetKeyboardMapping(_display, (uint)min, max - min + 1, out _perKeycode);
        Assert.That(map, Is.Not.EqualTo(nint.Zero));
        var keysyms = (ulong*)map;
        for (var i = 0; i <= max - min; i++)
        {
            var row = new ulong[_perKeycode];
            for (var j = 0; j < _perKeycode; j++) row[j] = keysyms[i * _perKeycode + j];
            if (row[0] is >= 'a' and <= 'z') _saved.Add(((uint)(min + i), row));
        }
        _ = NativeMethods.XFree(map);
        Assume.That(_saved, Has.Count.EqualTo(26), "pre-condition: us's 26 letter keys");
        for (var i = 0; i < _saved.Count; i++)
        {
            var row = new ulong[_perKeycode];
            row[0] = XorgKeysym.Of((char)('а' + i));
            row[1] = XorgKeysym.Of((char)('А' + i));
            _ = NativeMethods.XChangeKeyboardMapping(_display, (int)_saved[i].Keycode, _perKeycode, row, 1);
        }
        _ = NativeMethods.XSync(_display, false);
    }
}

using Hydra.Platform.Linux;

namespace Tests.Keyboard;

// XkbKeymapLookup and EvdevKeyResolver.FromKeymap over keymap text compiled from RMLVO names. they need
// libxkbcommon, so they self-skip off Linux and run in the test-linux CI job and run-tests.sh linux.
[TestFixture]
[Category("Linux")]
public class XkbKeymapLookupTests
{
    private const uint KeyA = 30, Key2 = 3, Key7 = 8, KeyQ = 16, KeyKp1 = 79, KeyEuro = 435;
    private const uint KpOne = 0xFFB1, KpEnd = 0xFF9C;

    [OneTimeSetUp]
    public void RequireLinux()
    {
        if (!OperatingSystem.IsLinux())
            Assert.Ignore("Requires Linux with libxkbcommon (run ./run-tests.sh linux)");
    }

    private static XkbKeymapLookup Lookup(string layout) =>
        new(XkbKeymapLookup.KeymapTextFromNames(new LinuxInputConfig.XkbNames(layout, "pc105", null, null)));

    [Test]
    public void Us_LowercaseA_IsKeyAUnshifted()
    {
        using var lookup = Lookup("us");
        Assert.That(lookup.Find(XkbKeymapLookup.CharToKeysym('a')), Is.EqualTo(new XkbKeyStroke(KeyA, false, false)));
    }

    [Test]
    public void Us_UppercaseA_IsKeyAWithShift()
    {
        using var lookup = Lookup("us");
        Assert.That(lookup.Find(XkbKeymapLookup.CharToKeysym('A')), Is.EqualTo(new XkbKeyStroke(KeyA, true, false)));
    }

    [Test]
    public void Us_At_IsKey2WithShift()
    {
        using var lookup = Lookup("us");
        Assert.That(lookup.Find(XkbKeymapLookup.CharToKeysym('@')), Is.EqualTo(new XkbKeyStroke(Key2, true, false)));
    }

    [Test]
    public void De_At_IsKeyQWithAltGr()
    {
        using var lookup = Lookup("de");
        Assert.That(lookup.Find(XkbKeymapLookup.CharToKeysym('@')), Is.EqualTo(new XkbKeyStroke(KeyQ, false, true)));
    }

    [Test]
    public void De_OpeningBrace_IsKey7WithAltGr()
    {
        using var lookup = Lookup("de");
        Assert.That(lookup.Find(XkbKeymapLookup.CharToKeysym('{')), Is.EqualTo(new XkbKeyStroke(Key7, false, true)));
    }

    [Test]
    public void De_Euro_IsTheDedicatedEuroKey()
    {
        // pc105 keymaps carry KEY_EURO, whose level 0 beats AltGr+E
        using var lookup = Lookup("de");
        Assert.That(lookup.Find(XkbKeymapLookup.CharToKeysym('€')), Is.EqualTo(new XkbKeyStroke(KeyEuro, false, false)));
    }

    [Test]
    public void Us_Kp1_IsTheKeypadKeyWithNumLock()
    {
        using var lookup = Lookup("us");
        Assert.That(lookup.Find(KpOne), Is.EqualTo(new XkbKeyStroke(KeyKp1, false, false, NumLock: true)));
    }

    [Test]
    public void Us_KpEnd_IsTheKeypadKeyWithoutNumLock()
    {
        using var lookup = Lookup("us");
        Assert.That(lookup.Find(KpEnd), Is.EqualTo(new XkbKeyStroke(KeyKp1, false, false)));
    }

    [Test]
    public void No_At_IsKey2WithAltGr()
    {
        using var lookup = Lookup("no");
        Assert.That(lookup.Find(XkbKeymapLookup.CharToKeysym('@')), Is.EqualTo(new XkbKeyStroke(Key2, false, true)));
    }

    [Test]
    public void Us_KeysymNotInKeymap_IsNull()
    {
        using var lookup = Lookup("us");
        using (Assert.EnterMultipleScope())
        {
            Assert.That(lookup.Find(XkbKeymapLookup.CharToKeysym('ø')), Is.Null);
            Assert.That(lookup.EvdevKeyFor(XkbKeymapLookup.CharToKeysym('ø')), Is.Null);
        }
    }

    [Test]
    public void EvdevKeyFor_IsTheStrokesKey()
    {
        using var lookup = Lookup("us");
        Assert.That(lookup.EvdevKeyFor(XkbKeymapLookup.CharToKeysym('A')), Is.EqualTo(KeyA));
    }

    [Test]
    public void Us_UnboundKey_TypesNothing()
    {
        using var lookup = Lookup("us");
        var key = lookup.UnboundKey();
        Assert.That(key, Is.Not.Null);
        Assert.That(key is { } k ? lookup.BaseKeysym(k) : 1u, Is.Zero.Or.EqualTo(0xFFFFFFu));
    }

    [Test]
    public void Us_UnboundKey_IsInTheRangeEveryVirtualKeyboardTakes()
    {
        // mutter drops a key past KEY_KBDINPUTASSIST_CANCEL; xkb's X11 range (evdev 247) is safe everywhere
        using var lookup = Lookup("us");
        Assert.That(lookup.UnboundKey(), Is.Not.Null.And.LessThanOrEqualTo(247u));
    }

    [Test]
    public void FromKeymap_ResolvesKeyA_LikeTheNamesResolver()
    {
        var names = new LinuxInputConfig.XkbNames("us", "pc105", null, null);
        using var fromNames = new EvdevKeyResolver(names);
        using var fromText = EvdevKeyResolver.FromKeymap(XkbKeymapLookup.KeymapTextFromNames(names));

        var expected = fromNames.Resolve(KeyA, 1)!.Concat(fromNames.Resolve(KeyA, 0)!).Select(e => (e!.Character, e.PhysicalKey)).ToArray();
        var actual = fromText.Resolve(KeyA, 1)!.Concat(fromText.Resolve(KeyA, 0)!).Select(e => (e!.Character, e.PhysicalKey)).ToArray();

        using (Assert.EnterMultipleScope())
        {
            Assert.That(actual, Is.EqualTo(expected));
            Assert.That(actual.Select(e => e.Character), Is.EqualTo(new char?[] { 'a', 'a' }));
        }
    }

    [Test]
    public void SharedKeymap_ServesLookupResolverAndModifierState()
    {
        using var keymap = XkbKeymap.FromNames(new LinuxInputConfig.XkbNames("us", "pc105", null, null));
        using var lookup = new XkbKeymapLookup(keymap);
        using var resolver = new EvdevKeyResolver(keymap);
        using var state = new Hydra.Platform.Linux.Wayland.XkbModifierState(keymap);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(lookup.Find(XkbKeymapLookup.CharToKeysym('a')), Is.EqualTo(new XkbKeyStroke(KeyA, false, false)));
            Assert.That(resolver.Resolve(KeyA, 1)![0]!.Character, Is.EqualTo('a'));
            Assert.That(state.Mask("Shift"), Is.Not.Zero);
        }
    }

    [Test]
    public void SharedKeymap_OutlivesTheUsersDisposedBeforeIt()
    {
        using var keymap = XkbKeymap.FromNames(new LinuxInputConfig.XkbNames("us", "pc105", null, null));
        new XkbKeymapLookup(keymap).Dispose();
        new EvdevKeyResolver(keymap).Dispose();

        using var lookup = new XkbKeymapLookup(keymap);
        Assert.That(lookup.Find(XkbKeymapLookup.CharToKeysym('a')), Is.EqualTo(new XkbKeyStroke(KeyA, false, false)));
    }

    [Test]
    public void SharedKeymap_TextCompilesBackToTheSameLookup()
    {
        using var keymap = XkbKeymap.FromNames(new LinuxInputConfig.XkbNames("no", "pc105", null, null));
        using var fromText = XkbKeymap.FromText(keymap.Text());
        using var lookup = new XkbKeymapLookup(fromText);
        Assert.That(lookup.Find(XkbKeymapLookup.CharToKeysym('ø')), Is.Not.Null);
    }

    [Test]
    public void SharedKeymap_BadText_Throws() =>
        Assert.Throws<InvalidOperationException>(() => XkbKeymap.FromText("not a keymap"));
}

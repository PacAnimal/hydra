using System.Runtime.CompilerServices;
using Hydra.Keyboard;
using Hydra.Mouse;
using Hydra.Platform.Linux;
using Hydra.Platform.Linux.Wayland;
using Hydra.Relay;
using Hydra.Screen;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Time.Testing;
using Tests.Setup;

namespace Tests.Platform.Wayland;

// WaylandInjection, the translation behind the Wayland slave: keys on a keymap compiled from RMLVO names, buttons,
// scroll and absolute motion. the keymap needs libxkbcommon, so the fixture self-skips off Linux and runs in the
// test-linux CI job and run-tests.sh linux.
[TestFixture]
[Category("Linux")]
public class WaylandOutputHandlerTests
{
    private const uint KeyA = 30, Key2 = 3, Key7 = 8, KeyQ = 16, KeyF1 = 59, KeyHome = 102, KeyKp1 = 79, KeyEuro = 435;
    private const uint LeftShift = 42, RightShift = 54, LeftCtrl = 29, RightCtrl = 97, LeftAlt = 56, RightAlt = 100;
    private const uint LeftMeta = 125, RightMeta = 126;

    [OneTimeSetUp]
    public void RequireLinux()
    {
        if (!OperatingSystem.IsLinux())
            Assert.Ignore("Requires Linux with libxkbcommon (run ./run-tests.sh linux)");
    }

    private static XkbKeymapLookup Lookup(string layout) =>
        new(XkbKeymapLookup.KeymapTextFromNames(new LinuxInputConfig.XkbNames(layout, "pc105", null, null)));

    private static KeyEventMessage Char(char ch, KeyModifiers mods = KeyModifiers.None) =>
        new(KeyEventType.KeyDown, mods, ch, null);

    private static KeyEventMessage Key(SpecialKey key, KeyModifiers mods = KeyModifiers.None) =>
        new(KeyEventType.KeyDown, mods, null, key);

    private static EiKeyPlan? Plan(XkbKeymapLookup keymap, KeyEventMessage msg, HeldModifierKeys? held = null, bool capsLock = false) =>
        WaylandInjection.Plan(keymap, msg, held ?? HeldModifierKeys.None, capsLock);

    // the key the keymap puts ISO_Level3_Shift on first: RALT, or the virtual <LVL3> key where the keymap has one
    private static uint Level3(XkbKeymapLookup keymap) =>
        keymap.EvdevKeyFor(0xFE03) ?? throw new AssertionException("pre-condition: the keymap has ISO_Level3_Shift");

    private static EiKeyStroke Down(uint key) => new(key, true);
    private static EiKeyStroke Up(uint key) => new(key, false);

    [Test]
    public void LowercaseLetter_IsItsKeyAlone()
    {
        using var keymap = Lookup("us");
        var plan = Plan(keymap, Char('a'));
        using (Assert.EnterMultipleScope())
        {
            Assert.That(plan?.Key, Is.EqualTo(KeyA));
            Assert.That(plan?.Strokes, Is.EqualTo([Down(KeyA)]));
        }
    }

    [Test]
    public void ShiftedCharacter_WithShiftHeld_IsItsKeyAlone()
    {
        using var keymap = Lookup("us");
        var plan = Plan(keymap, Char('A', KeyModifiers.Shift), new HeldModifierKeys([LeftShift], []));
        Assert.That(plan?.Strokes, Is.EqualTo([Down(KeyA)]));
    }

    [Test]
    public void ShiftedCharacter_WithNoShiftHeld_PressesShiftAroundIt()
    {
        using var keymap = Lookup("us");
        var plan = Plan(keymap, Char('@'));
        using (Assert.EnterMultipleScope())
        {
            Assert.That(plan?.Key, Is.EqualTo(Key2));
            Assert.That(plan?.Strokes, Is.EqualTo([Down(LeftShift), Down(Key2), Up(LeftShift)]));
        }
    }

    [Test]
    public void UnshiftedCharacter_WithShiftHeld_LiftsShiftAroundIt()
    {
        // '2' typed while the master holds Shift: its layout put '2' on a shifted key, ours does not
        using var keymap = Lookup("us");
        var plan = Plan(keymap, Char('2', KeyModifiers.Shift), new HeldModifierKeys([RightShift], []));
        Assert.That(plan?.Strokes, Is.EqualTo([Up(RightShift), Down(Key2), Down(RightShift)]));
    }

    [Test]
    public void Shortcut_KeepsHeldShift()
    {
        // Ctrl+Shift+2 is a shortcut: the Shift held for it stays down
        using var keymap = Lookup("us");
        var plan = Plan(keymap, Char('2', KeyModifiers.Shift | KeyModifiers.Control), new HeldModifierKeys([LeftShift], []));
        Assert.That(plan?.Strokes, Is.EqualTo([Down(Key2)]));
    }

    [Test]
    public void AltGrCharacter_PressesTheKeymapsLevel3Key()
    {
        using var keymap = Lookup("de");
        var level3 = Level3(keymap);
        var plan = Plan(keymap, Char('{', KeyModifiers.AltGr));
        using (Assert.EnterMultipleScope())
        {
            Assert.That(plan?.Key, Is.EqualTo(Key7));
            Assert.That(plan?.Strokes, Is.EqualTo([Down(level3), Down(Key7), Up(level3)]));
        }
    }

    [Test]
    public void Euro_WithADedicatedKey_IsThatKeyAlone()
    {
        using var keymap = Lookup("de");
        Assert.That(Plan(keymap, Char('€', KeyModifiers.AltGr))?.Strokes, Is.EqualTo([Down(KeyEuro)]));
    }

    [Test]
    public void AltGrCharacter_WithAltGrHeld_IsItsKeyAlone()
    {
        using var keymap = Lookup("de");
        var plan = Plan(keymap, Char('@', KeyModifiers.AltGr), new HeldModifierKeys([], [Level3(keymap)]));
        Assert.That(plan?.Strokes, Is.EqualTo([Down(KeyQ)]));
    }

    [Test]
    public void AltGrHeld_ForAPlainCharacter_IsLiftedAroundIt()
    {
        using var keymap = Lookup("de");
        var level3 = Level3(keymap);
        var plan = Plan(keymap, Char('q', KeyModifiers.AltGr), new HeldModifierKeys([], [level3]));
        Assert.That(plan?.Strokes, Is.EqualTo([Up(level3), Down(KeyQ), Down(level3)]));
    }

    [Test]
    public void Letter_UnderCapsLock_FlipsShift()
    {
        using var keymap = Lookup("us");
        using (Assert.EnterMultipleScope())
        {
            Assert.That(Plan(keymap, Char('A'), capsLock: true)?.Strokes, Is.EqualTo([Down(KeyA)]));
            Assert.That(Plan(keymap, Char('a'), capsLock: true)?.Strokes, Is.EqualTo([Down(LeftShift), Down(KeyA), Up(LeftShift)]));
        }
    }

    [Test]
    public void CharacterNotOnTheKeymap_IsNothing()
    {
        using var keymap = Lookup("us");
        Assert.That(Plan(keymap, Char('Ж')), Is.Null);
    }

    [TestCase(SpecialKey.F1, KeyF1)]
    [TestCase(SpecialKey.Home, KeyHome)]
    [TestCase(SpecialKey.MoveToBeginningOfLine, KeyHome)]
    public void SpecialKey_IsItsOwnKey(SpecialKey key, uint expected)
    {
        using var keymap = Lookup("us");
        var plan = Plan(keymap, Key(key));
        using (Assert.EnterMultipleScope())
        {
            Assert.That(plan?.Strokes, Is.EqualTo([Down(expected)]));
            Assert.That(plan?.NumLock, Is.Null, "NumLock left alone");
        }
    }

    // the keypad key's own keycode, under the NumLock X11 picks its level by (NumLock XOR Shift)
    [TestCase(SpecialKey.KP_1, KeyModifiers.None, true)]
    [TestCase(SpecialKey.KP_End, KeyModifiers.None, false)]
    [TestCase(SpecialKey.KP_1, KeyModifiers.Shift, false)]
    [TestCase(SpecialKey.KP_End, KeyModifiers.Shift, true)]
    public void KeypadKey_IsTheKeypadKeyUnderTheNumLockItNeeds(SpecialKey key, KeyModifiers mods, bool numLock)
    {
        using var keymap = Lookup("us");
        var plan = Plan(keymap, Key(key, mods));
        using (Assert.EnterMultipleScope())
        {
            Assert.That(plan?.Strokes, Is.EqualTo([Down(KeyKp1)]));
            Assert.That(plan?.NumLock, Is.EqualTo(numLock));
        }
    }

    [Test]
    public void SpecialKey_NeverWrapsInModifiers()
    {
        using var keymap = Lookup("us");
        var plan = Plan(keymap, Key(SpecialKey.Home, KeyModifiers.Shift), new HeldModifierKeys([LeftShift], []));
        Assert.That(plan?.Strokes, Is.EqualTo([Down(KeyHome)]));
    }

    [TestCase(SpecialKey.Shift_L, LeftShift)]
    [TestCase(SpecialKey.Shift_R, RightShift)]
    [TestCase(SpecialKey.Control_L, LeftCtrl)]
    [TestCase(SpecialKey.Control_R, RightCtrl)]
    [TestCase(SpecialKey.Alt_L, LeftAlt)]
    [TestCase(SpecialKey.Alt_R, RightAlt)]
    [TestCase(SpecialKey.Super_L, LeftMeta)]
    [TestCase(SpecialKey.Super_R, RightMeta)]
    public void Modifier_IsItsOwnSide(SpecialKey key, uint expected)
    {
        using var keymap = Lookup("us");
        Assert.That(Plan(keymap, Key(key))?.Key, Is.EqualTo(expected));
    }

    [Test]
    public void AltGr_IsTheKeymapsLevel3Key()
    {
        using var keymap = Lookup("de");
        Assert.That(Plan(keymap, Key(SpecialKey.AltGr))?.Key, Is.EqualTo(Level3(keymap)));
    }

    [Test]
    public void RightAlt_WhereAltGrSits_IsStillAnAltKey()
    {
        using var keymap = Lookup("de");
        var alt = keymap.EvdevKeyFor(0xFFEA) ?? keymap.EvdevKeyFor(0xFFE9);
        using (Assert.EnterMultipleScope())
        {
            Assert.That(alt, Is.Not.Null, "pre-condition: the keymap has an Alt key");
            Assert.That(Plan(keymap, Key(SpecialKey.Alt_R))?.Key, Is.EqualTo(alt));
        }
    }

    private static KeyEventMessage Release(SpecialKey key, bool? chorded = null) =>
        new(KeyEventType.KeyUp, KeyModifiers.None, null, key, Chorded: chorded);

    [TestCase(SpecialKey.Super_L, LeftMeta)]
    [TestCase(SpecialKey.Super_R, RightMeta)]
    public void ChordedSuperRelease_TapsAKeyThatTypesNothingFirst(SpecialKey super, uint superKey)
    {
        using var keymap = Lookup("us");
        var tap = keymap.UnboundKey() ?? throw new AssertionException("pre-condition: the keymap has a key that types nothing");
        Assert.That(WaylandInjection.Release(keymap, Release(super, chorded: true), superKey),
            Is.EqualTo([Down(tap), Up(tap), Up(superKey)]));
    }

    [TestCase(false)]
    [TestCase(null)]
    public void PlainSuperRelease_IsItsUpAlone(bool? chorded)
    {
        using var keymap = Lookup("us");
        Assert.That(WaylandInjection.Release(keymap, Release(SpecialKey.Super_L, chorded), LeftMeta), Is.EqualTo([Up(LeftMeta)]));
    }

    [Test]
    public void ChordedAltRelease_IsItsUpAlone()
    {
        // the X11 slave taps only for Super
        using var keymap = Lookup("us");
        Assert.That(WaylandInjection.Release(keymap, Release(SpecialKey.Alt_L, chorded: true), LeftAlt), Is.EqualTo([Up(LeftAlt)]));
    }

    [TestCase(MouseButton.Left, 0x110u)]
    [TestCase(MouseButton.Right, 0x111u)]
    [TestCase(MouseButton.Middle, 0x112u)]
    [TestCase(MouseButton.Extra1, 0x113u)]
    [TestCase(MouseButton.Extra2, 0x114u)]
    public void Button_IsItsEvdevButton(MouseButton button, uint expected) =>
        Assert.That(WaylandInjection.EvdevButton(button), Is.EqualTo(expected));

    [Test]
    public void UnknownButton_IsNone() => Assert.That(WaylandInjection.EvdevButton((MouseButton)9), Is.Null);

    [Test]
    public void ScrollUp_IsNegativeDiscreteY() =>
        Assert.That(WaylandInjection.Discrete(new MouseScrollMessage(0, MouseScrollEvent.Notch)), Is.EqualTo(new EiScroll(0, -120)));

    [Test]
    public void ScrollDown_IsPositiveDiscreteY() =>
        Assert.That(WaylandInjection.Discrete(new MouseScrollMessage(0, -2 * MouseScrollEvent.Notch)), Is.EqualTo(new EiScroll(0, 240)));

    [Test]
    public void ScrollRight_IsPositiveDiscreteX() =>
        Assert.That(WaylandInjection.Discrete(new MouseScrollMessage(MouseScrollEvent.Notch, 0)), Is.EqualTo(new EiScroll(120, 0)));

    [Test]
    public void SmoothScroll_KeepsItsFraction() =>
        Assert.That(WaylandInjection.Discrete(new MouseScrollMessage(-30, 40)), Is.EqualTo(new EiScroll(-30, -40)));

    private static readonly ScreenBounds[] TwoRegions =
    [
        new(0, 0, 1920, 1080),
        new(1920, 0, 1280, 1024),
    ];

    [Test]
    public void PointInTheFirstRegion_IsUnchanged() =>
        Assert.That(WaylandInjection.AbsoluteTarget(TwoRegions, 100, 200), Is.EqualTo(new EiPoint(100, 200)));

    [Test]
    public void PointInTheSecondRegion_IsUnchanged() =>
        Assert.That(WaylandInjection.AbsoluteTarget(TwoRegions, 2500, 900), Is.EqualTo(new EiPoint(2500, 900)));

    [Test]
    public void PointBelowTheShorterRegion_IsPulledOntoItsBottomRow() =>
        Assert.That(WaylandInjection.AbsoluteTarget(TwoRegions, 2500, 1050), Is.EqualTo(new EiPoint(2500, 1023)));

    [Test]
    public void PointPastTheRightEdge_IsPulledOntoTheLastColumn() =>
        Assert.That(WaylandInjection.AbsoluteTarget(TwoRegions, 3300, 10), Is.EqualTo(new EiPoint(3199, 10)));

    [Test]
    public void NoRegions_PassesThePointThrough() =>
        Assert.That(WaylandInjection.AbsoluteTarget([], 50, 60), Is.EqualTo(new EiPoint(50, 60)));

    // -- dead keys: de has dead_acute on KEY_EQUAL (13) and dead_grave Shifted there, dead_circumflex on KEY_GRAVE
    // (41), and neither é, ´ nor ` on a key of its own --

    private const uint KeyEqual = 13, KeyGrave = 41, KeyE = 18, KeySpace = 57;

    [Test]
    public void Eacute_OnDe_IsDeadAcuteThenE()
    {
        using var keymap = Lookup("de");
        Assert.That(keymap.Find(XkbKeymapLookup.CharToKeysym('é')), Is.Null, "pre-condition: de has no é key");
        var plan = Plan(keymap, Char('é'));
        using (Assert.EnterMultipleScope())
        {
            Assert.That(plan?.Key, Is.EqualTo(KeyE), "the base character is the key held until the master's up");
            Assert.That(plan?.Strokes, Is.EqualTo([Down(KeyEqual), Up(KeyEqual), Down(KeyE)]));
            Assert.That(plan?.NumLock, Is.Null);
        }
    }

    [Test]
    public void Eacute_OnUsIntl_HasADeadAcuteSequence()
    {
        using var keymap = new XkbKeymapLookup(XkbKeymapLookup.KeymapTextFromNames(new LinuxInputConfig.XkbNames("us", "pc105", "intl", null)));
        using (Assert.EnterMultipleScope())
        {
            Assert.That(keymap.EvdevKeyFor(0xFE51), Is.EqualTo(40u), "pre-condition: dead_acute is the apostrophe key");
            Assert.That(WaylandInjection.DeadKeySequence(keymap, 'é'),
                Is.EqualTo([new WaylandInjection.ComposeStep(0xFE51, false), new WaylandInjection.ComposeStep(0x65, true)]));
            // us(intl) also has é on AltGr+e, which a key of its own types before any sequence
            Assert.That(Plan(keymap, Char('é'))?.Strokes, Is.EqualTo([Down(Level3(keymap)), Down(KeyE), Up(Level3(keymap))]));
        }
    }

    [Test]
    public void UppercaseEacute_OnDe_ShiftsOnlyTheBase()
    {
        using var keymap = Lookup("de");
        Assert.That(Plan(keymap, Char('É', KeyModifiers.Shift))?.Strokes,
            Is.EqualTo([Down(KeyEqual), Up(KeyEqual), Down(LeftShift), Down(KeyE), Up(LeftShift)]));
    }

    [Test]
    public void Eacute_WithShiftHeld_LiftsItForTheDeadKeyAndTheBase()
    {
        using var keymap = Lookup("de");
        Assert.That(Plan(keymap, Char('é', KeyModifiers.Shift), new HeldModifierKeys([LeftShift], []))?.Strokes,
            Is.EqualTo([Up(LeftShift), Down(KeyEqual), Up(KeyEqual), Down(LeftShift), Up(LeftShift), Down(KeyE), Down(LeftShift)]));
    }

    [Test]
    public void TwoMarks_OnDe_AreBothDeadKeysOutermostFirst()
    {
        // ấ is a + circumflex + acute: dead_acute dead_circumflex a, as the Compose table has it
        using var keymap = Lookup("de");
        Assert.That(Plan(keymap, Char('ấ'))?.Strokes,
            Is.EqualTo([Down(KeyEqual), Up(KeyEqual), Down(KeyGrave), Up(KeyGrave), Down(KeyA)]));
    }

    [Test]
    public void SpacingAcute_OnDe_IsDeadAcuteTwice()
    {
        // dead_acute space composes an apostrophe; the Compose table types ´ as dead_acute dead_acute
        using var keymap = Lookup("de");
        using (Assert.EnterMultipleScope())
        {
            Assert.That(keymap.Find(0xB4), Is.Null, "pre-condition: de has no ´ key");
            Assert.That(Plan(keymap, Char('´'))?.Strokes, Is.EqualTo([Down(KeyEqual), Up(KeyEqual), Down(KeyEqual)]));
        }
    }

    [Test]
    public void SpacingGrave_OnDe_IsShiftedDeadGraveThenSpace()
    {
        using var keymap = Lookup("de");
        Assert.That(Plan(keymap, Char('`'))?.Strokes,
            Is.EqualTo([Down(LeftShift), Down(KeyEqual), Up(KeyEqual), Up(LeftShift), Down(KeySpace)]));
    }

    [Test]
    public void ACharacterWithNoDecomposition_IsNotPlanned()
    {
        using var keymap = Lookup("de");
        using (Assert.EnterMultipleScope())
        {
            Assert.That(Plan(keymap, Char('☃')), Is.Null);
            Assert.That(WaylandInjection.DeadKeySequence(keymap, '☃'), Is.Null);
        }
    }

    [Test]
    public void ALayoutWithoutDeadKeys_DoesNotComposeIt()
    {
        // us has no dead keys at all, so é cannot be composed there
        using var keymap = Lookup("us");
        Assert.That(Plan(keymap, Char('é')), Is.Null);
    }

    [TestCase('\u0301', 0xFE51u)]
    [TestCase('\u0300', 0xFE50u)]
    [TestCase('\u0302', 0xFE52u)]
    [TestCase('\u0303', 0xFE53u)]
    [TestCase('\u0308', 0xFE57u)]
    [TestCase('\u030A', 0xFE58u)]
    [TestCase('\u0327', 0xFE5Bu)]
    [TestCase('\u030C', 0xFE5Au)]
    [TestCase('\u0304', 0xFE54u)]
    [TestCase('\u0306', 0xFE55u)]
    [TestCase('\u0328', 0xFE5Cu)]
    [TestCase('\u0307', 0xFE56u)]
    [TestCase('\u030B', 0xFE59u)]
    public void CombiningMark_IsItsDeadKeysym(char mark, uint keysym) =>
        Assert.That(WaylandInjection.DeadKeysym(mark), Is.EqualTo(keysym));

    // -- a keysym bound to a spare key, for the wlroots device --

    [Test]
    public void SpareKey_TypesNothingAndIsNotTheUnboundKey()
    {
        using var keymap = Lookup("us");
        var spare = keymap.SpareKey() ?? throw new AssertionException("pre-condition: us has a second unbound key");
        using (Assert.EnterMultipleScope())
        {
            Assert.That(spare, Is.Not.EqualTo(keymap.UnboundKey()));
            Assert.That(spare, Is.LessThanOrEqualTo(247u));
            Assert.That(keymap.BaseKeysym(spare), Is.Zero.Or.EqualTo(0xFFFFFFu));
        }
    }

    [Test]
    public void BoundKeymap_CompilesAndTypesTheKeysymOnTheSpareKey()
    {
        const uint snowman = 0x1002603;
        var text = XkbKeymapLookup.KeymapTextFromNames(new LinuxInputConfig.XkbNames("us", "pc105", null, null));
        using var keymap = new XkbKeymapLookup(text);
        var spare = keymap.SpareKey() ?? throw new AssertionException("pre-condition: us has a spare key");
        var bound = KeymapBinding.Bind(text, spare, snowman) ?? throw new AssertionException("the rewrite did not compile");
        using var rebound = new XkbKeymapLookup(bound);
        using (Assert.EnterMultipleScope())
        {
            Assert.That(rebound.Find(snowman), Is.EqualTo(new XkbKeyStroke(spare, false, false)));
            Assert.That(rebound.EvdevKeyFor(0x61), Is.EqualTo(KeyA), "every other key is as it was");
            Assert.That(rebound.UnboundKey(), Is.EqualTo(keymap.UnboundKey()));
        }
    }

    [Test]
    public void BoundKeymap_ForAKeycodeTheKeycodesSectionLeavesUnnamed_NamesIt()
    {
        const uint snowman = 0x1002603;
        var text = XkbKeymapLookup.KeymapTextFromNames(new LinuxInputConfig.XkbNames("us", "pc105", null, null));
        var named = System.Text.RegularExpressions.Regex.Replace(text, @"^\s*<I248>\s*=\s*248;\n", "", System.Text.RegularExpressions.RegexOptions.Multiline);
        Assert.That(named, Does.Not.Contain("<I248>"), "pre-condition: the keycode lost its name");
        var bound = KeymapBinding.Bind(named, 240, snowman) ?? throw new AssertionException("the rewrite did not compile");
        using var rebound = new XkbKeymapLookup(bound);
        Assert.That(rebound.Find(snowman), Is.EqualTo(new XkbKeyStroke(240, false, false)));
    }

    [Test]
    public void BoundKeymap_PastTheKeycodesMaximum_IsNull()
    {
        var text = XkbKeymapLookup.KeymapTextFromNames(new LinuxInputConfig.XkbNames("us", "pc105", null, null));
        Assert.That(KeymapBinding.Rewrite(text, 100000, 0x1002603), Is.Null);
    }
}

// when the Wayland slave asks the session for its injection device: a backoff after a failed start, and a device
// obtained across a session loss discarded. no libxkbcommon or portal, so these run everywhere.
[TestFixture]
public class WaylandSenderAcquisitionTests
{
    private readonly FakeTimeProvider _time = new();
    private FakeWaylandSession _session = null!;
    private WaylandOutputHandler? _handler;

    [SetUp]
    public void Create() => _session = new FakeWaylandSession();

    [TearDown]
    public void Dispose()
    {
        _handler?.Dispose();
        _session.Dispose();
    }

    private async Task<WaylandOutputHandler> Started(CapturingLog<WaylandOutputHandler>? log = null)
    {
        _handler = new WaylandOutputHandler(_session, log ?? new CapturingLog<WaylandOutputHandler>(), _time);
        await _session.Asked(1);
        return _handler;
    }

    [Test]
    public void FailedStart_WaitsFiveSecondsThenDoubles()
    {
        var acquisition = new SenderAcquisition(_time);
        var delays = new List<TimeSpan>();
        for (var i = 0; i < 6; i++)
        {
            var epoch = acquisition.Begin() ?? throw new AssertionException($"attempt {i} refused");
            delays.Add(acquisition.Failed(epoch)?.Delay ?? TimeSpan.Zero);
            _time.Advance(delays[^1]);
        }
        TimeSpan[] expected = [.. Seconds(5, 10, 20, 40, 60, 60)];
        Assert.That(delays, Is.EqualTo(expected));
    }

    private static IEnumerable<TimeSpan> Seconds(params int[] seconds) => seconds.Select(s => TimeSpan.FromSeconds(s));

    [Test]
    public void FailedStart_RefusesAnotherUntilItsDelayIsOver()
    {
        var acquisition = new SenderAcquisition(_time);
        acquisition.Failed(acquisition.Begin() ?? 0);
        _time.Advance(SenderAcquisition.FirstRetry - TimeSpan.FromMilliseconds(1));
        Assert.That(acquisition.Begin(), Is.Null);
        _time.Advance(TimeSpan.FromMilliseconds(1));
        Assert.That(acquisition.Begin(), Is.Not.Null);
    }

    [Test]
    public void OnlyTheFirstFailureOfAStreak_IsFirst()
    {
        var acquisition = new SenderAcquisition(_time);
        var first = acquisition.Failed(acquisition.Begin() ?? 0);
        _time.Advance(SenderAcquisition.MaxRetry);
        var second = acquisition.Failed(acquisition.Begin() ?? 0);
        _time.Advance(SenderAcquisition.MaxRetry);
        acquisition.Succeeded(acquisition.Begin() ?? 0);
        var afterSuccess = acquisition.Failed(acquisition.Begin() ?? 0);
        using (Assert.EnterMultipleScope())
        {
            Assert.That(new[] { first?.FirstOfStreak, second?.FirstOfStreak, afterSuccess?.FirstOfStreak }, Is.EqualTo(new bool?[] { true, false, true }));
            Assert.That(afterSuccess?.Delay, Is.EqualTo(SenderAcquisition.FirstRetry), "a success resets the backoff");
        }
    }

    [Test]
    public void AttemptRunning_RefusesASecond()
    {
        var acquisition = new SenderAcquisition(_time);
        Assert.That(acquisition.Begin(), Is.Not.Null);
        Assert.That(acquisition.Begin(), Is.Null);
    }

    [Test]
    public void SenderFromBeforeASessionLoss_IsDiscarded()
    {
        var acquisition = new SenderAcquisition(_time);
        var epoch = acquisition.Begin() ?? 0;
        acquisition.Lost();
        using (Assert.EnterMultipleScope())
        {
            Assert.That(acquisition.Succeeded(epoch), Is.False);
            Assert.That(acquisition.Begin(), Is.Not.Null, "the loss lets a fresh attempt start at once");
        }
    }

    [Test]
    public void FailureFromBeforeASessionLoss_SetsNoBackoff()
    {
        var acquisition = new SenderAcquisition(_time);
        var epoch = acquisition.Begin() ?? 0;
        acquisition.Lost();
        using (Assert.EnterMultipleScope())
        {
            Assert.That(acquisition.Failed(epoch), Is.Null);
            Assert.That(acquisition.Begin(), Is.Not.Null);
        }
    }

    [Test]
    public async Task Handler_AfterAFailedStart_DoesNotAskAgainOnInputUntilTheBackoffIsOver()
    {
        var handler = await Started();
        _session.Fail(1);
        await handler.Acquiring;

        handler.MoveMouse(1, 1);
        handler.MoveMouseRelative(1, 1);
        Assert.That(_session.Calls, Is.EqualTo(1), "no new start inside the backoff");

        _time.Advance(SenderAcquisition.FirstRetry);
        handler.MoveMouse(2, 2);
        await _session.Asked(2);
        Assert.That(_session.Calls, Is.EqualTo(2));
    }

    [Test]
    public async Task Handler_WarnsOncePerFailureStreak()
    {
        var log = new CapturingLog<WaylandOutputHandler>();
        var handler = await Started(log);
        _session.Fail(1);
        await handler.Acquiring;
        _time.Advance(SenderAcquisition.FirstRetry);
        handler.MoveMouse(1, 1);
        await _session.Asked(2);
        _session.Fail(2);
        await handler.Acquiring;

        Assert.That(log.Entries.Count(e => e.Level == LogLevel.Warning && e.Message.Contains("Cannot start input injection")), Is.EqualTo(1));
    }

    [Test]
    public async Task Handler_DeviceObtainedAcrossASessionLoss_IsNotKept()
    {
        var handler = await Started();
        _session.RaiseSessionLost();
        using var device = new RecordingDevice();
        _session.Succeed(1, device);
        await handler.Acquiring;

        Assert.That(handler.HasDevice, Is.False);
        handler.MoveMouse(1, 1);
        await _session.Asked(2);
        using (Assert.EnterMultipleScope())
        {
            Assert.That(_session.Calls, Is.EqualTo(2), "the next input starts the new session's device");
            Assert.That(device.Sent, Is.Empty);
        }
    }

    [Test]
    public async Task SharedStart_AfterAFailure_StartsAgainOnlyOnceTheBackoffIsOver()
    {
        var starts = new StrongBox<int>();
        var start = new SharedStart<object>(() =>
        {
            Interlocked.Increment(ref starts.Value);
            return Task.FromException<object>(new PortalException("the user cancelled the dialog"));
        }, _time);
        Assert.That(async () => await start.Get(), Throws.InstanceOf<PortalException>());

        // as two clipboard resolves right after it would
        var first = start.Get();
        var second = start.Get();
        using (Assert.EnterMultipleScope())
        {
            Assert.That(first.IsFaulted && second.IsFaulted, Is.True, "both answered with the failure");
            Assert.That(Volatile.Read(ref starts.Value), Is.EqualTo(1), "no new start inside the backoff");
        }

        _time.Advance(SenderAcquisition.FirstRetry);
        Assert.That(async () => await start.Get(), Throws.InstanceOf<PortalException>());
        Assert.That(Volatile.Read(ref starts.Value), Is.EqualTo(2));
    }

    private static readonly ScreenBounds[] Regions = [new(0, 0, 1920, 1080), new(1920, 0, 1280, 1024)];

    private async Task<WaylandOutputHandler> WithDevice(RecordingDevice device) => await Answer(await Started(), 1, device);

    private async Task<WaylandOutputHandler> Answer(WaylandOutputHandler handler, int call, RecordingDevice device)
    {
        await _session.Asked(call);
        _session.Succeed(call, device);
        await handler.Acquiring;
        Assert.That(handler.HasDevice, Is.True, "pre-condition: the handler took the device");
        return handler;
    }

    [Test]
    public async Task FirstMove_GoesThroughANeighbouringPixelFirst_EachInItsOwnFrame()
    {
        using var device = new RecordingDevice(regions: Regions);
        var handler = await WithDevice(device);
        handler.MoveMouse(960, 540);
        using (Assert.EnterMultipleScope())
        {
            Assert.That(device.Sent, Is.EqualTo(["start", "abs 961,540", "frame", "abs 960,540", "frame"]));
            Assert.That(handler.GetCursorPosition(), Is.EqualTo(new ScreenPoint(960, 540)));
        }
    }

    [Test]
    public async Task SecondMove_IsTheTargetAlone()
    {
        using var device = new RecordingDevice(regions: Regions);
        var handler = await WithDevice(device);
        handler.MoveMouse(960, 540);
        handler.MoveMouse(100, 200);
        Assert.That(device.Sent, Is.EqualTo(["start", "abs 961,540", "frame", "abs 960,540", "frame", "abs 100,200", "frame"]));
    }

    [Test]
    public async Task FirstMove_OnADeviceAcquiredAgainAfterALoss_GoesThroughANeighbouringPixelAgain()
    {
        using var lost = new RecordingDevice(regions: Regions);
        var handler = await WithDevice(lost);
        handler.MoveMouse(960, 540);
        lost.Lose();

        handler.MoveMouse(960, 540);
        using var again = new RecordingDevice(regions: Regions);
        await Answer(handler, 2, again);
        handler.MoveMouse(960, 540);
        Assert.That(again.Sent, Is.EqualTo(["start", "abs 961,540", "frame", "abs 960,540", "frame"]));
    }

    [Test]
    public async Task FirstMove_AtARegionsRightEdge_GoesThroughItsLeftNeighbour()
    {
        using var device = new RecordingDevice(regions: Regions);
        var handler = await WithDevice(device);
        handler.MoveMouse(3199, 500);
        Assert.That(device.Sent, Is.EqualTo(["start", "abs 3198,500", "frame", "abs 3199,500", "frame"]));
    }

    // every entry can land where the cursor rests, not only the device's first: a master's leave nudges the next move
    [Test]
    public async Task FirstMove_AfterAMasterLeaves_GoesThroughANeighbouringPixelAgain()
    {
        using var device = new RecordingDevice(regions: Regions);
        var handler = await WithDevice(device);
        handler.MoveMouse(960, 540);

        handler.EndVisit();
        handler.MoveMouse(960, 540);

        Assert.That(device.Sent, Is.EqualTo(["start", "abs 961,540", "frame", "abs 960,540", "frame", "stop",
            "start", "abs 961,540", "frame", "abs 960,540", "frame"]));
    }
}

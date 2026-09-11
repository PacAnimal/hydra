using Hydra.Config;
using Hydra.Keyboard;

namespace Tests.Config;

[TestFixture]
public class HotkeyTests
{
    private const KeyModifiers Chord = KeyModifiers.Control | KeyModifiers.Alt | KeyModifiers.Super;

    private static KeyEvent Char(char ch, KeyModifiers mods) => KeyEvent.Char(KeyEventType.KeyDown, ch, mods);
    private static KeyEvent Special(SpecialKey key, KeyModifiers mods = KeyModifiers.None) => KeyEvent.Special(KeyEventType.KeyDown, key, mods);

    [TestCase('l', HotkeyAction.ToggleCursorLock)]
    [TestCase('k', HotkeyAction.LockSlaves)]
    [TestCase('m', HotkeyAction.ToggleRelativeMouse)]
    [TestCase('c', HotkeyAction.CopyFiles)]
    [TestCase('v', HotkeyAction.PasteFiles)]
    [TestCase('z', HotkeyAction.MissionControl)]
    public void Defaults_KeepTheOriginalChord(char ch, HotkeyAction expected)
    {
        var hotkeys = HotkeyBindings.Build(null);
        using (Assert.EnterMultipleScope())
        {
            Assert.That(hotkeys.Errors, Is.Empty);
            Assert.That(hotkeys.Match(Char(ch, Chord)), Is.EqualTo(expected));
        }
    }

    [Test]
    public void UnboundKey_MatchesNothing()
    {
        var hotkeys = HotkeyBindings.Build(null);
        using (Assert.EnterMultipleScope())
        {
            Assert.That(hotkeys.Match(Char('q', Chord)), Is.Null);
            Assert.That(hotkeys.Match(Char('l', KeyModifiers.None)), Is.Null, "plain 'l' must stay typeable");
            Assert.That(hotkeys.Match(Char('l', KeyModifiers.Control)), Is.Null, "a partial chord must not fire");
        }
    }

    [Test]
    public void LockStates_DoNotBreakAChord()
    {
        // CapsLock/NumLock/ScrollLock ride along in Modifiers as lock state, not as part of the chord
        var hotkeys = HotkeyBindings.Build(null);
        Assert.That(hotkeys.Match(Char('l', Chord | KeyModifiers.CapsLock | KeyModifiers.NumLock)),
            Is.EqualTo(HotkeyAction.ToggleCursorLock));
    }

    [Test]
    public void SingleNamedKey_NeedsNoModifiers()
    {
        var hotkeys = HotkeyBindings.Build(new() { ["toggleCursorLock"] = ["ScrollLock"] });
        Assert.That(hotkeys.Errors, Is.Empty);
        using (Assert.EnterMultipleScope())
        {
            Assert.That(hotkeys.Match(Special(SpecialKey.ScrollLock)), Is.EqualTo(HotkeyAction.ToggleCursorLock));
            // pressing ScrollLock also sets its own lock bit on that very event
            Assert.That(hotkeys.Match(Special(SpecialKey.ScrollLock, KeyModifiers.ScrollLock)), Is.EqualTo(HotkeyAction.ToggleCursorLock));
            Assert.That(hotkeys.Match(Char('l', Chord)), Is.Null, "a configured array replaces the default");
        }
    }

    [Test]
    public void MultipleBindings_BothFireTheSameAction()
    {
        var hotkeys = HotkeyBindings.Build(new() { ["toggleCursorLock"] = ["Ctrl+Alt+Super+L", "ScrollLock"] });
        Assert.That(hotkeys.Errors, Is.Empty);
        using (Assert.EnterMultipleScope())
        {
            Assert.That(hotkeys.Match(Char('l', Chord)), Is.EqualTo(HotkeyAction.ToggleCursorLock));
            Assert.That(hotkeys.Match(Special(SpecialKey.ScrollLock)), Is.EqualTo(HotkeyAction.ToggleCursorLock));
        }
    }

    [Test]
    public void OverridingOneAction_LeavesTheOthersAlone()
    {
        var hotkeys = HotkeyBindings.Build(new() { ["toggleCursorLock"] = ["F13"] });
        using (Assert.EnterMultipleScope())
        {
            Assert.That(hotkeys.Match(Special(SpecialKey.F13)), Is.EqualTo(HotkeyAction.ToggleCursorLock));
            Assert.That(hotkeys.Match(Char('v', Chord)), Is.EqualTo(HotkeyAction.PasteFiles));
        }
    }

    [TestCase("Cmd+Alt+Ctrl+L")]
    [TestCase("win+option+control+l")]
    [TestCase("  Super + Alt + Ctrl + L  ")]
    [TestCase("L+Ctrl+Alt+Super")]
    public void ModifierAliases_AndOrder_AndCase_AllResolveTheSame(string spec)
    {
        var hotkeys = HotkeyBindings.Build(new() { ["toggleCursorLock"] = [spec] });
        using (Assert.EnterMultipleScope())
        {
            Assert.That(hotkeys.Errors, Is.Empty, $"'{spec}' should parse");
            Assert.That(hotkeys.Match(Char('l', Chord)), Is.EqualTo(HotkeyAction.ToggleCursorLock));
        }
    }

    [TestCase("Ctrl+Home", SpecialKey.Home, KeyModifiers.Control)]
    [TestCase("Alt+PageDown", SpecialKey.PageDown, KeyModifiers.Alt)]
    [TestCase("Win+F1", SpecialKey.F1, KeyModifiers.Super)]
    [TestCase("Ctrl+Shift+End", SpecialKey.End, KeyModifiers.Control | KeyModifiers.Shift)]
    [TestCase("Alt+Up", SpecialKey.Up, KeyModifiers.Alt)]
    [TestCase("Super+KP_5", SpecialKey.KP_5, KeyModifiers.Super)]
    [TestCase("Ctrl+AudioMute", SpecialKey.AudioMute, KeyModifiers.Control)]
    [TestCase("Alt+Insert", SpecialKey.Insert, KeyModifiers.Alt)]
    [TestCase("Ctrl+Alt+F12", SpecialKey.F12, KeyModifiers.Control | KeyModifiers.Alt)]
    public void NamedKeys_CombineWithModifiers(string spec, SpecialKey key, KeyModifiers mods)
    {
        var hotkeys = HotkeyBindings.Build(new() { ["lockSlaves"] = [spec] });
        using (Assert.EnterMultipleScope())
        {
            Assert.That(hotkeys.Errors, Is.Empty, $"'{spec}' should parse");
            Assert.That(hotkeys.Match(Special(key, mods)), Is.EqualTo(HotkeyAction.LockSlaves));
        }
    }

    [Test]
    public void ActionNameIsCaseInsensitive()
    {
        var hotkeys = HotkeyBindings.Build(new() { ["TOGGLECURSORLOCK"] = ["F13"] });
        using (Assert.EnterMultipleScope())
        {
            Assert.That(hotkeys.Errors, Is.Empty);
            Assert.That(hotkeys.Match(Special(SpecialKey.F13)), Is.EqualTo(HotkeyAction.ToggleCursorLock));
        }
    }

    [Test]
    public void PlusCanBeBound()
    {
        var hotkeys = HotkeyBindings.Build(new() { ["lockSlaves"] = ["Ctrl+Alt++"] });
        using (Assert.EnterMultipleScope())
        {
            Assert.That(hotkeys.Errors, Is.Empty);
            Assert.That(hotkeys.Match(Char('+', KeyModifiers.Control | KeyModifiers.Alt)), Is.EqualTo(HotkeyAction.LockSlaves));
        }
    }

    [TestCase("c", "would stop that character ever being typed")]
    [TestCase("Ctrl+Alt", "no key to press")]
    [TestCase("Ctrl+Shift_L", "swallow every press")]
    [TestCase("Ctrl+Nonsense", "no key named")]
    [TestCase("Ctrl+A+B", "names two keys")]
    public void BadBinding_IsRejectedAndTheDefaultSurvives(string spec, string expectedError)
    {
        var hotkeys = HotkeyBindings.Build(new() { ["toggleCursorLock"] = [spec] });
        using (Assert.EnterMultipleScope())
        {
            Assert.That(hotkeys.Errors, Has.One.Contains(expectedError));
            Assert.That(hotkeys.Match(Char('l', Chord)), Is.EqualTo(HotkeyAction.ToggleCursorLock),
                "a rejected binding must leave the action on its default, not unreachable");
        }
    }

    [Test]
    public void UnknownAction_IsReportedNotIgnoredSilently()
    {
        var hotkeys = HotkeyBindings.Build(new() { ["togleCursorLock"] = ["F13"] });
        Assert.That(hotkeys.Errors, Has.One.Contains("unknown hotkey action"));
    }

    [Test]
    public void EmptyList_KeepsTheDefault()
    {
        var hotkeys = HotkeyBindings.Build(new() { ["toggleCursorLock"] = [] });
        using (Assert.EnterMultipleScope())
        {
            Assert.That(hotkeys.Errors, Has.One.Contains("is empty"));
            Assert.That(hotkeys.Match(Char('l', Chord)), Is.EqualTo(HotkeyAction.ToggleCursorLock));
        }
    }
}

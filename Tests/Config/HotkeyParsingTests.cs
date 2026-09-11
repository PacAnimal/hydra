using Hydra.Config;
using Hydra.Keyboard;
using Tests.Setup;

namespace Tests.Config;

// unit tests for the chord parser itself; HotkeyTests covers the table built on top of it
[TestFixture]
public class HotkeyParsingTests
{
    private static readonly string ConfigurationDocs =
        File.ReadAllText(Path.Combine(TestLog.SolutionRoot, "docs", "CONFIGURATION.md"));

    private static HotkeyBinding Parse(string spec)
    {
        Assert.That(HotkeyBinding.TryParse(spec, out var binding, out var error), Is.True, $"'{spec}' should parse but: {error}");
        return binding!;
    }

    private static string Reject(string spec)
    {
        Assert.That(HotkeyBinding.TryParse(spec, out _, out var error), Is.False, $"'{spec}' should have been rejected");
        return error!;
    }

    [TestCase("ctrl", KeyModifiers.Control)]
    [TestCase("control", KeyModifiers.Control)]
    [TestCase("alt", KeyModifiers.Alt)]
    [TestCase("opt", KeyModifiers.Alt)]
    [TestCase("option", KeyModifiers.Alt)]
    [TestCase("shift", KeyModifiers.Shift)]
    [TestCase("super", KeyModifiers.Super)]
    [TestCase("win", KeyModifiers.Super)]
    [TestCase("windows", KeyModifiers.Super)]
    [TestCase("cmd", KeyModifiers.Super)]
    [TestCase("command", KeyModifiers.Super)]
    [TestCase("meta", KeyModifiers.Super)]
    [TestCase("altgr", KeyModifiers.AltGr)]
    public void EveryModifierAlias_MapsToItsFlag(string alias, KeyModifiers expected)
    {
        Assert.That(Parse($"{alias}+X").Modifiers, Is.EqualTo(expected));
    }

    [TestCase("Ctrl+Alt+Super+L", KeyModifiers.Control | KeyModifiers.Alt | KeyModifiers.Super)]
    [TestCase("Ctrl+Shift+X", KeyModifiers.Control | KeyModifiers.Shift)]
    [TestCase("Shift+Alt+Super+AltGr+X", KeyModifiers.Shift | KeyModifiers.Alt | KeyModifiers.Super | KeyModifiers.AltGr)]
    public void ModifiersCombine(string spec, KeyModifiers expected)
    {
        Assert.That(Parse(spec).Modifiers, Is.EqualTo(expected));
    }

    [Test]
    public void RepeatedModifier_IsHarmless()
    {
        Assert.That(Parse("Ctrl+Ctrl+control+L").Modifiers, Is.EqualTo(KeyModifiers.Control));
    }

    [TestCase("CTRL+ALT+SUPER+L")]
    [TestCase("ctrl+alt+super+l")]
    [TestCase("Ctrl+Alt+Super+l")]
    [TestCase("  ctrl + alt + super + L  ")]
    [TestCase("L+super+alt+ctrl")]
    public void CaseWhitespaceAndOrder_DoNotMatter(string spec)
    {
        var binding = Parse(spec);
        using (Assert.EnterMultipleScope())
        {
            Assert.That(binding.Modifiers, Is.EqualTo(KeyModifiers.Control | KeyModifiers.Alt | KeyModifiers.Super));
            Assert.That(binding.Character, Is.EqualTo('l'));
            Assert.That(binding.Key, Is.Null);
        }
    }

    // the documented key list is the SpecialKey enum, so every bindable member must parse by name --
    // this is what stops the docs drifting from what the resolvers actually emit
    [TestCaseSource(nameof(BindableSpecialKeys))]
    public void EveryBindableSpecialKey_ParsesByName(string name)
    {
        var binding = Parse($"Ctrl+{name}");
        using (Assert.EnterMultipleScope())
        {
            Assert.That(binding.Key, Is.EqualTo(Enum.Parse<SpecialKey>(name)));
            Assert.That(binding.Character, Is.Null);
        }
    }

    private static IEnumerable<string> BindableSpecialKeys() =>
        Enum.GetNames<SpecialKey>().Where(name => Enum.Parse<SpecialKey>(name) is not (
            SpecialKey.Shift_L or SpecialKey.Shift_R or SpecialKey.Control_L or SpecialKey.Control_R
            or SpecialKey.Alt_L or SpecialKey.Alt_R or SpecialKey.Super_L or SpecialKey.Super_R or SpecialKey.AltGr));

    // the reference list in the docs is hand-written, so it can only stay correct if something checks it
    [TestCaseSource(nameof(BindableSpecialKeys))]
    public void EveryBindableSpecialKey_IsDocumented(string name)
    {
        Assert.That(ConfigurationDocs, Does.Contain($"`{name}`"),
            $"SpecialKey.{name} can be bound but is missing from the Bindable keys list in docs/CONFIGURATION.md");
    }

    [TestCase("Pause", SpecialKey.Pause)]
    [TestCase("PrintScreen", SpecialKey.PrintScreen)]
    [TestCase("Ctrl+Pause", SpecialKey.Pause)]
    [TestCase("Alt+PrintScreen", SpecialKey.PrintScreen)]
    [TestCase("Menu", SpecialKey.Menu)]
    [TestCase("Shift+Menu", SpecialKey.Menu)]
    public void PcExtraKeys_AreBindable(string spec, SpecialKey expected)
    {
        Assert.That(Parse(spec).Key, Is.EqualTo(expected));
    }

    [TestCase("Ctrl+ScrollLock", SpecialKey.ScrollLock)]
    [TestCase("ScrollLock", SpecialKey.ScrollLock)]
    [TestCase("NumLock", SpecialKey.NumLock)]
    [TestCase("CapsLock", SpecialKey.CapsLock)]
    public void LockKeys_AreBindable_UnlikeChordModifiers(string spec, SpecialKey expected)
    {
        // SpecialKey.IsModifier() counts the lock keys too, but ScrollLock alone is the headline use case
        Assert.That(Parse(spec).Key, Is.EqualTo(expected));
    }

    [TestCase("Ctrl+Space", ' ')]
    [TestCase("Ctrl+Plus", '+')]
    [TestCase("Ctrl+Minus", '-')]
    [TestCase("Ctrl+Comma", ',')]
    [TestCase("Ctrl+Period", '.')]
    [TestCase("Ctrl+Dot", '.')]
    [TestCase("Ctrl+Slash", '/')]
    [TestCase("Ctrl+Backslash", '\\')]
    [TestCase("Ctrl+Semicolon", ';')]
    [TestCase("Ctrl+Quote", '\'')]
    [TestCase("Ctrl+Apostrophe", '\'')]
    [TestCase("Ctrl+Grave", '`')]
    [TestCase("Ctrl+Backtick", '`')]
    [TestCase("Ctrl+Equals", '=')]
    [TestCase("Ctrl+BracketLeft", '[')]
    [TestCase("Ctrl+BracketRight", ']')]
    public void CharacterKeys_CanBeNamed(string spec, char expected)
    {
        var binding = Parse(spec);
        using (Assert.EnterMultipleScope())
        {
            Assert.That(binding.Character, Is.EqualTo(expected));
            Assert.That(binding.Key, Is.Null);
        }
    }

    [TestCase("Ctrl+4", '4')]
    [TestCase("Ctrl+/", '/')]
    [TestCase("Ctrl+;", ';')]
    [TestCase("Ctrl+Alt++", '+')]
    [TestCase("Ctrl+Å", 'å')]
    public void LiteralCharacters_AreTakenAsTyped(string spec, char expected)
    {
        Assert.That(Parse(spec).Character, Is.EqualTo(expected));
    }

    [TestCase("", "is empty")]
    [TestCase("   ", "is empty")]
    [TestCase("Ctrl", "no key to press")]
    [TestCase("Ctrl+Alt+Super", "no key to press")]
    [TestCase("Ctrl+Nope", "no key named")]
    [TestCase("Ctrl+F21", "no key named")]
    [TestCase("Ctrl+A+B", "names two keys")]
    [TestCase("Ctrl+Home+End", "names two keys")]
    [TestCase("c", "would stop that character ever being typed")]
    [TestCase("4", "would stop that character ever being typed")]
    [TestCase("Space", "would stop that character ever being typed")]
    [TestCase("+", "would stop that character ever being typed")]
    [TestCase("Ctrl+Shift_L", "swallow every press")]
    [TestCase("Ctrl+Super_R", "swallow every press")]
    [TestCase("Ctrl+AltGr", "no key to press")]  // altgr is a modifier alias, so nothing is left over
    public void BadSpec_IsRejectedWithAReasonThatNamesTheProblem(string spec, string expectedError)
    {
        Assert.That(Reject(spec), Does.Contain(expectedError));
    }

    [Test]
    public void Matches_RequiresTheExactModifierSet()
    {
        var binding = Parse("Ctrl+Alt+L");
        using (Assert.EnterMultipleScope())
        {
            Assert.That(binding.Matches(KeyEvent.Char(KeyEventType.KeyDown, 'l', KeyModifiers.Control | KeyModifiers.Alt)), Is.True);
            Assert.That(binding.Matches(KeyEvent.Char(KeyEventType.KeyDown, 'l', KeyModifiers.Control)), Is.False, "too few modifiers");
            Assert.That(binding.Matches(KeyEvent.Char(KeyEventType.KeyDown, 'l',
                KeyModifiers.Control | KeyModifiers.Alt | KeyModifiers.Shift)), Is.False, "an extra modifier is a different chord");
            Assert.That(binding.Matches(KeyEvent.Char(KeyEventType.KeyDown, 'x', KeyModifiers.Control | KeyModifiers.Alt)), Is.False);
        }
    }

    [TestCase(KeyModifiers.CapsLock)]
    [TestCase(KeyModifiers.NumLock)]
    [TestCase(KeyModifiers.ScrollLock)]
    [TestCase(KeyModifiers.CapsLock | KeyModifiers.NumLock | KeyModifiers.ScrollLock)]
    public void Matches_IgnoresLockState(KeyModifiers locks)
    {
        var binding = Parse("Ctrl+Alt+L");
        Assert.That(binding.Matches(KeyEvent.Char(KeyEventType.KeyDown, 'l', KeyModifiers.Control | KeyModifiers.Alt | locks)), Is.True);
    }

    [Test]
    public void Matches_IsCaseInsensitiveOnTheCharacter()
    {
        // a chord including Shift resolves to an uppercase char on some layouts
        var binding = Parse("Ctrl+Shift+L");
        Assert.That(binding.Matches(KeyEvent.Char(KeyEventType.KeyDown, 'L', KeyModifiers.Control | KeyModifiers.Shift)), Is.True);
    }

    [Test]
    public void Matches_CoversKeyUpToo()
    {
        // InputRouter consumes both halves so the slave never sees a stray release
        var binding = Parse("Ctrl+Alt+L");
        Assert.That(binding.Matches(KeyEvent.Char(KeyEventType.KeyUp, 'l', KeyModifiers.Control | KeyModifiers.Alt)), Is.True);
    }

    [Test]
    public void Matches_DoesNotConfuseANamedKeyWithACharacter()
    {
        var named = Parse("Ctrl+Home");
        var literal = Parse("Ctrl+H");
        using (Assert.EnterMultipleScope())
        {
            Assert.That(named.Matches(KeyEvent.Char(KeyEventType.KeyDown, 'h', KeyModifiers.Control)), Is.False);
            Assert.That(literal.Matches(KeyEvent.Special(KeyEventType.KeyDown, SpecialKey.Home, KeyModifiers.Control)), Is.False);
        }
    }
}

using Hydra.Config;
using Hydra.Keyboard;
using Hydra.Screen;
using Tests.Setup;

namespace Tests.Screen;

// the monitor switch's keys: a direction, optionally a perpendicular second held with it, fired on releasing either
[TestFixture]
public class MonitorSwitchGestureTests
{
    private static readonly HotkeyBindings Defaults = HotkeyBindings.Build(null);

    private MonitorSwitchGesture _gesture = null!;

    [SetUp]
    public void SetUp() => _gesture = new MonitorSwitchGesture();

    [Test]
    public void OneDirection_SwitchesOnItsRelease()
    {
        using (Assert.EnterMultipleScope())
        {
            Assert.That(Down(SpecialKey.Up), Is.Null, "nothing moves until the key is let go");
            Assert.That(Up(SpecialKey.Up), Is.EqualTo(new SwitchRequest(Direction.Up, null)));
        }
    }

    [TestCase(SpecialKey.Up, SpecialKey.Right, Direction.Up, Direction.Right)]
    [TestCase(SpecialKey.Right, SpecialKey.Up, Direction.Right, Direction.Up)]
    [TestCase(SpecialKey.Left, SpecialKey.Down, Direction.Left, Direction.Down)]
    [TestCase(SpecialKey.Down, SpecialKey.Left, Direction.Down, Direction.Left)]
    public void APerpendicularSecond_PicksTheThird_WhicheverIsReleasedFirst(SpecialKey first, SpecialKey second, Direction edge, Direction third)
    {
        foreach (var releasedFirst in new[] { first, second })
        {
            _gesture = new MonitorSwitchGesture();
            Down(first);
            Down(second);
            var released = new[] { first, second }.Single(k => k != releasedFirst);

            using (Assert.EnterMultipleScope())
            {
                Assert.That(Up(releasedFirst), Is.EqualTo(new SwitchRequest(edge, third)));
                Assert.That(Up(released), Is.Null, "the other key's release switches nothing more");
            }
        }
    }

    // a modifier let go before the arrow leaves the arrow's release unmatched by any chord, but it still switches
    [Test]
    public void AModifierReleasedFirst_StillSwitches()
    {
        Down(SpecialKey.Up);

        Assert.That(_gesture.OnKey(Key(KeyEventType.KeyUp, SpecialKey.Up, KeyModifiers.Alt | KeyModifiers.Super), null),
            Is.EqualTo(new SwitchRequest(Direction.Up, null)));
    }

    [TestCase(SpecialKey.Down)]
    [TestCase(SpecialKey.Up)]
    public void ADirectionAlongTheSameAxis_AbandonsIt(SpecialKey second)
    {
        Down(SpecialKey.Up);
        Down(second, physical: 99);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(Up(SpecialKey.Up), Is.Null);
            Assert.That(Up(second, physical: 99), Is.Null);
        }
    }

    [Test]
    public void AThirdDirection_IsIgnored()
    {
        Down(SpecialKey.Up);
        Down(SpecialKey.Right);
        Down(SpecialKey.Left);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(Up(SpecialKey.Left), Is.Null, "the third key is no part of it");
            Assert.That(Up(SpecialKey.Up), Is.EqualTo(new SwitchRequest(Direction.Up, Direction.Right)));
        }
    }

    [Test]
    public void AnotherKeyPressedMidway_AbandonsIt()
    {
        Down(SpecialKey.Up);
        _gesture.OnKey(Key(KeyEventType.KeyDown, SpecialKey.Return, HotkeyChord.Modifiers), null);

        Assert.That(Up(SpecialKey.Up), Is.Null);
    }

    // a second direction counts only with the first's modifiers
    [Test]
    public void ASecondWithOtherModifiers_IsNoPartOfIt()
    {
        var bindings = HotkeyBindings.Build(new() { ["switchMonitorRight"] = ["Ctrl+Alt+Super+Right", "F14"] });
        Down(SpecialKey.Up);
        var f14 = Key(KeyEventType.KeyDown, SpecialKey.F14, KeyModifiers.Control | KeyModifiers.Alt | KeyModifiers.Super);
        _gesture.OnKey(f14 with { Modifiers = KeyModifiers.None }, bindings.Match(f14 with { Modifiers = KeyModifiers.None }));

        Assert.That(Up(SpecialKey.Up), Is.EqualTo(new SwitchRequest(Direction.Up, null)));
    }

    [Test]
    public void Repeats_ChangeNothing()
    {
        Down(SpecialKey.Up);
        _gesture.OnKey(Key(KeyEventType.KeyDown, SpecialKey.Up, HotkeyChord.Modifiers) with { IsRepeat = true }, HotkeyAction.SwitchMonitorUp);
        _gesture.OnKey(Key(KeyEventType.KeyDown, SpecialKey.Return, HotkeyChord.Modifiers) with { IsRepeat = true }, null);

        Assert.That(Up(SpecialKey.Up), Is.EqualTo(new SwitchRequest(Direction.Up, null)));
    }

    [Test]
    public void AReleaseWithNothingHeld_SwitchesNothing() => Assert.That(Up(SpecialKey.Up), Is.Null);

    [Test]
    public void AfterASwitch_TheNextOneStartsFresh()
    {
        Down(SpecialKey.Up);
        Up(SpecialKey.Up);
        Down(SpecialKey.Left);

        Assert.That(Up(SpecialKey.Left), Is.EqualTo(new SwitchRequest(Direction.Left, null)));
    }

    private SwitchRequest? Down(SpecialKey key, int? physical = null)
    {
        var ev = Key(KeyEventType.KeyDown, key, HotkeyChord.Modifiers, physical);
        return _gesture.OnKey(ev, Defaults.Match(ev));
    }

    private SwitchRequest? Up(SpecialKey key, int? physical = null)
    {
        var ev = Key(KeyEventType.KeyUp, key, HotkeyChord.Modifiers, physical);
        return _gesture.OnKey(ev, Defaults.Match(ev));
    }

    // stamped with a physical key of its own, as a resolver stamps every key
    private static KeyEvent Key(KeyEventType type, SpecialKey key, KeyModifiers mods, int? physical = null) =>
        KeyEvent.Special(type, key, mods) with { PhysicalKey = physical ?? (int)key };
}

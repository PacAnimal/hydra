using Hydra.Config;
using Hydra.Keyboard;
using Hydra.Relay;
using Tests.Setup;

namespace Tests.Keyboard;

// modifierNumLock as the router applies it: what reaches the slave, and what the hotkeys see
[TestFixture]
public class ModifierNumLockForwardingTests : ServicesFixture
{
    private const int Kp4 = 75;

    private async Task<TestServiceBundle> Visiting(bool? modifierNumLock, Dictionary<string, List<string>>? hotkeys = null)
    {
        var bundle = await Services.Visiting(TransitionTestHelper.CreateService(
            profile: TransitionTestHelper.ProfileWith(hotkeys: hotkeys, modifierNumLock: modifierNumLock)));
        bundle.Relay.Sent.Clear();
        return bundle;
    }

    private static KeyEvent Kp(KeyEventType type, SpecialKey key, KeyModifiers mods, bool repeat = false) =>
        KeyEvent.Special(type, key, mods) with { PhysicalKey = Kp4, IsRepeat = repeat };

    [Test]
    public async Task True_TheSlaveGetsTheDigit_DownRepeatAndUp_WithNumLockSet()
    {
        var bundle = await Visiting(true);

        bundle.Platform.FireKeyEvent(Kp(KeyEventType.KeyDown, SpecialKey.KP_Left, KeyModifiers.Super));
        bundle.Platform.FireKeyEvent(Kp(KeyEventType.KeyDown, SpecialKey.KP_Left, KeyModifiers.None, repeat: true));
        bundle.Platform.FireKeyEvent(Kp(KeyEventType.KeyUp, SpecialKey.KP_Left, KeyModifiers.None));

        var sent = bundle.Relay.Sent.Of<KeyEventMessage>(MessageKind.KeyEvent);
        using (Assert.EnterMultipleScope())
        {
            Assert.That(sent.Select(m => m.Key), Is.EqualTo([SpecialKey.KP_4, SpecialKey.KP_4, SpecialKey.KP_4]));
            Assert.That(sent[0].Modifiers, Is.EqualTo(KeyModifiers.Super | KeyModifiers.NumLock));
        }
    }

    [Test]
    public async Task Null_TheSlaveGetsTheKeyAsPressed()
    {
        var bundle = await Visiting(null);

        bundle.Platform.FireKeyEvent(Kp(KeyEventType.KeyDown, SpecialKey.KP_Left, KeyModifiers.Super));

        var sent = bundle.Relay.Sent.Of<KeyEventMessage>(MessageKind.KeyEvent);
        Assert.That(sent.Select(m => (m.Key, m.Modifiers)), Is.EqualTo([((SpecialKey?)SpecialKey.KP_Left, KeyModifiers.Super)]));
    }

    private static readonly Dictionary<string, List<string>> Super4Locks = new()
    {
        [nameof(HotkeyAction.ToggleCursorLock)] = ["Super+KP_4"],
    };

    [Test]
    public async Task True_AHotkeyBoundToTheDigit_FiresOnTheNavigationKey_AndTakesIt()
    {
        var bundle = await Visiting(true, Super4Locks);

        bundle.Platform.FireKeyEvent(Kp(KeyEventType.KeyDown, SpecialKey.KP_Left, KeyModifiers.Super));

        Assert.That(bundle.Relay.Sent.Of<KeyEventMessage>(MessageKind.KeyEvent), Is.Empty, "the hotkey consumed it");
    }

    [Test]
    public async Task Null_AHotkeyBoundToTheDigit_DoesNotFireOnTheNavigationKey()
    {
        var bundle = await Visiting(null, Super4Locks);

        bundle.Platform.FireKeyEvent(Kp(KeyEventType.KeyDown, SpecialKey.KP_Left, KeyModifiers.Super));

        Assert.That(bundle.Relay.Sent.Of<KeyEventMessage>(MessageKind.KeyEvent).Select(m => m.Key), Is.EqualTo([SpecialKey.KP_Left]));
    }

    private static void ReturnHome(TestServiceBundle bundle)
    {
        for (var i = 0; i < 60 && bundle.Platform.IsOnVirtualScreen; i++)
            bundle.Platform.FireMouseDelta(-100, 0);
        Assert.That(bundle.Platform.IsOnVirtualScreen, Is.False, "pre-condition: home again");
    }

    [Test]
    public async Task AKeyPressedAtHome_AndReleasedDuringAVisit_IsNeverReleasedOnTheSlave()
    {
        var bundle = TransitionTestHelper.CreateService(profile: TransitionTestHelper.ProfileWith(modifierNumLock: true));
        await Services.StartOnline(bundle);

        bundle.Platform.FireKeyEvent(Kp(KeyEventType.KeyDown, SpecialKey.KP_Left, KeyModifiers.Super));
        TransitionTestHelper.EnterRemote(bundle.Platform);
        bundle.Relay.Sent.Clear();
        bundle.Platform.FireKeyEvent(Kp(KeyEventType.KeyDown, SpecialKey.KP_Left, KeyModifiers.None, repeat: true));
        bundle.Platform.FireKeyEvent(Kp(KeyEventType.KeyUp, SpecialKey.KP_Left, KeyModifiers.None));

        Assert.That(bundle.Relay.Sent.Of<KeyEventMessage>(MessageKind.KeyEvent), Is.Empty, "its repeat and up stay home with its down");
    }

    [Test]
    public async Task AKeyRewrittenOnAVisit_AndReleasedAtHome_CrossesPlain_WhenNextPressedPlain()
    {
        var bundle = await Visiting(true);

        bundle.Platform.FireKeyEvent(Kp(KeyEventType.KeyDown, SpecialKey.KP_Left, KeyModifiers.Super));
        ReturnHome(bundle);
        bundle.Platform.FireKeyEvent(Kp(KeyEventType.KeyUp, SpecialKey.KP_Left, KeyModifiers.None));
        TransitionTestHelper.EnterRemote(bundle.Platform);
        bundle.Relay.Sent.Clear();
        bundle.Platform.FireKeyEvent(Kp(KeyEventType.KeyDown, SpecialKey.KP_Left, KeyModifiers.None));
        bundle.Platform.FireKeyEvent(Kp(KeyEventType.KeyUp, SpecialKey.KP_Left, KeyModifiers.None));

        var sent = bundle.Relay.Sent.Of<KeyEventMessage>(MessageKind.KeyEvent);
        Assert.That(sent.Select(m => (m.Key, m.Modifiers)),
            Is.EqualTo([((SpecialKey?)SpecialKey.KP_Left, KeyModifiers.None), (SpecialKey.KP_Left, KeyModifiers.None)]));
    }
}

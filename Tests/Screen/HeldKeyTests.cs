using System.Text.Json;
using Cathedral.Config;
using Hydra.Config;
using Hydra.Keyboard;
using Hydra.Relay;
using Tests.Setup;

namespace Tests.Screen;

// A key held when the cursor crosses an edge is not carried across: its repeats and its up stay home, and only keys
// pressed during the visit reach the slave, down to up.
[TestFixture]
public class HeldKeyTests
{
    private const int KeyA = 30, KeyE = 18, ShiftL = 42, CtrlL = 29, AltL = 56, SuperL = 125, KeyL = 38, KeyM = 50;
    private const KeyModifiers Chord = KeyModifiers.Control | KeyModifiers.Alt | KeyModifiers.Super;

    private static KeyEvent Char(KeyEventType type, char ch, int key, KeyModifiers mods = KeyModifiers.None) =>
        KeyEvent.Char(type, ch, mods) with { PhysicalKey = key };

    private static KeyEvent Special(KeyEventType type, SpecialKey special, int key, KeyModifiers mods = KeyModifiers.None) =>
        KeyEvent.Special(type, special, mods) with { PhysicalKey = key };

    private static KeyEvent Repeat(char ch, int key, KeyModifiers mods = KeyModifiers.None) =>
        Char(KeyEventType.KeyDown, ch, key, mods) with { IsRepeat = true };

    private static async Task<TestServiceBundle> Started(IHydraProfile? profile = null)
    {
        var bundle = TransitionTestHelper.CreateService(profile: profile);
        await bundle.Service.StartAsync(CancellationToken.None);
        if (profile is null) await TransitionTestHelper.BringRemoteOnline(bundle.Relay);
        else await TransitionTestHelper.BringHostsOnline(bundle.Relay, ["remote", "remote2"]);
        return bundle;
    }

    private static void Enter(FakePlatform platform)
    {
        platform.FireMouseMove(2559, 720);
        Assert.That(platform.IsOnVirtualScreen, Is.True, "pre-condition: on the remote screen");
    }

    private static List<KeyEventMessage> ForwardedKeys(FakeRelay relay, string? host = null) =>
        [.. relay.Snapshot()
            .Where(s => s.Kind == MessageKind.KeyEvent && (host is null || s.Targets.Contains(host)))
            .Select(s => JsonSerializer.Deserialize<KeyEventMessage>(s.Json, SaneJson.Options)!)];

    private static async Task Stop(TestServiceBundle bundle)
    {
        await bundle.Service.StopAsync(CancellationToken.None);
        await bundle.Platform.DisposeAsync();
    }

    [Test]
    public async Task KeyHeldBeforeAHotkeyChord_ItsUpThatMatchesTheChordIsStillForwarded()
    {
        // m is down on the slave before the chord forms; its up then matches Ctrl+Alt+Super+M and must still release it
        var bundle = await Started();
        Enter(bundle.Platform);
        bundle.Platform.FireKeyEvent(Char(KeyEventType.KeyDown, 'm', KeyM));
        bundle.Platform.FireKeyEvent(Special(KeyEventType.KeyDown, SpecialKey.Control_L, CtrlL, KeyModifiers.Control));
        bundle.Platform.FireKeyEvent(Special(KeyEventType.KeyDown, SpecialKey.Alt_L, AltL, KeyModifiers.Control | KeyModifiers.Alt));
        bundle.Platform.FireKeyEvent(Special(KeyEventType.KeyDown, SpecialKey.Super_L, SuperL, Chord));
        bundle.Relay.ClearSent();

        bundle.Platform.FireKeyEvent(Char(KeyEventType.KeyUp, 'm', KeyM, Chord));

        var forwarded = ForwardedKeys(bundle.Relay);
        await Stop(bundle);
        Assert.That(forwarded.Select(k => (k.Type, k.Character)), Is.EqualTo([(KeyEventType.KeyUp, (char?)'m')]));
    }

    [Test]
    public async Task HotkeyTriggerPressedWithTheChord_ItsUpIsNotForwarded()
    {
        var bundle = await Started();
        Enter(bundle.Platform);
        bundle.Platform.FireKeyEvent(Special(KeyEventType.KeyDown, SpecialKey.Control_L, CtrlL, KeyModifiers.Control));
        bundle.Platform.FireKeyEvent(Special(KeyEventType.KeyDown, SpecialKey.Alt_L, AltL, KeyModifiers.Control | KeyModifiers.Alt));
        bundle.Platform.FireKeyEvent(Special(KeyEventType.KeyDown, SpecialKey.Super_L, SuperL, Chord));
        bundle.Platform.FireKeyEvent(Char(KeyEventType.KeyDown, 'm', KeyM, Chord));
        bundle.Relay.ClearSent();

        bundle.Platform.FireKeyEvent(Char(KeyEventType.KeyUp, 'm', KeyM, Chord));

        var forwarded = ForwardedKeys(bundle.Relay);
        await Stop(bundle);
        Assert.That(forwarded, Is.Empty);
    }

    [Test]
    public async Task PressedWhileLocal_ReleasedDuringTheVisit_TheUpIsNotForwarded()
    {
        var bundle = await Started();
        bundle.Platform.FireKeyEvent(Special(KeyEventType.KeyDown, SpecialKey.Control_L, CtrlL, KeyModifiers.Control));
        Enter(bundle.Platform);
        bundle.Relay.ClearSent();

        bundle.Platform.FireKeyEvent(Special(KeyEventType.KeyUp, SpecialKey.Control_L, CtrlL));

        Assert.That(ForwardedKeys(bundle.Relay), Is.Empty);
        await Stop(bundle);
    }

    [Test]
    public async Task PressedWhileLocal_ItsRepeatsDuringTheVisitAreNotForwarded()
    {
        var bundle = await Started();
        bundle.Platform.FireKeyEvent(Char(KeyEventType.KeyDown, 'a', KeyA));
        Enter(bundle.Platform);
        bundle.Relay.ClearSent();

        bundle.Platform.FireKeyEvent(Repeat('a', KeyA));
        bundle.Platform.FireKeyEvent(Repeat('a', KeyA));
        bundle.Platform.FireKeyEvent(Char(KeyEventType.KeyUp, 'a', KeyA));

        Assert.That(ForwardedKeys(bundle.Relay), Is.Empty);
        await Stop(bundle);
    }

    // also a down hooked before the flip that the router only processes after the entry: it is the visit's
    [Test]
    public async Task PressedAndReleasedDuringTheVisit_BothAreForwarded()
    {
        var bundle = await Started();
        Enter(bundle.Platform);
        bundle.Relay.ClearSent();

        bundle.Platform.FireKeyEvent(Char(KeyEventType.KeyDown, 'a', KeyA));
        bundle.Platform.FireKeyEvent(Repeat('a', KeyA));
        bundle.Platform.FireKeyEvent(Char(KeyEventType.KeyUp, 'a', KeyA));

        var sent = ForwardedKeys(bundle.Relay);
        Assert.That(sent.Select(k => (k.Type, k.Character, k.IsRepeat)),
            Is.EqualTo([(KeyEventType.KeyDown, 'a', false), (KeyEventType.KeyDown, 'a', true), (KeyEventType.KeyUp, 'a', false)]));
        await Stop(bundle);
    }

    [Test]
    public async Task PressedDuringTheVisit_RepeatsReResolvedUnderShiftAreForwarded()
    {
        var bundle = await Started();
        Enter(bundle.Platform);
        bundle.Relay.ClearSent();

        bundle.Platform.FireKeyEvent(Char(KeyEventType.KeyDown, 'e', KeyE));
        bundle.Platform.FireKeyEvent(Special(KeyEventType.KeyDown, SpecialKey.Shift_L, ShiftL, KeyModifiers.Shift));
        bundle.Platform.FireKeyEvent(Repeat('E', KeyE, KeyModifiers.Shift));
        bundle.Platform.FireKeyEvent(Char(KeyEventType.KeyUp, 'e', KeyE, KeyModifiers.Shift));

        Assert.That(ForwardedKeys(bundle.Relay).Select(k => (k.Type, k.Character, k.IsRepeat)), Is.EqualTo([
            (KeyEventType.KeyDown, (char?)'e', false), (KeyEventType.KeyDown, null, false),
            (KeyEventType.KeyDown, 'E', true), (KeyEventType.KeyUp, 'e', false)]));
        await Stop(bundle);
    }

    [Test]
    public async Task PressedOnOneHost_ItsRepeatsAndUpAreNotForwardedToTheNext()
    {
        var bundle = await Started(TransitionTestHelper.ChainConfig);
        Enter(bundle.Platform);
        bundle.Platform.FireKeyEvent(Char(KeyEventType.KeyDown, 'a', KeyA));
        for (var i = 0; i < 30; i++)
            bundle.Platform.FireMouseDelta(100, 0);
        Assert.That(bundle.Relay.Snapshot().Any(s => s.Kind == MessageKind.EnterScreen && s.Targets.Contains("remote2")), Is.True,
            "pre-condition: on remote2");
        bundle.Relay.ClearSent();

        bundle.Platform.FireKeyEvent(Repeat('a', KeyA));
        bundle.Platform.FireKeyEvent(Char(KeyEventType.KeyUp, 'a', KeyA));

        Assert.That(ForwardedKeys(bundle.Relay), Is.Empty);
        await Stop(bundle);
    }

    [Test]
    public async Task PressedOnOneMonitor_ItsUpIsForwardedOnTheSameHostsOtherMonitor()
    {
        var (platform, relay, service) = TransitionTestHelper.CreateRemoteOnlyService(TransitionTestHelper.RemoteOnlyPairConfig, headless: true);
        await service.StartAsync(CancellationToken.None);
        await relay.FirePeersChanged("mac", "win");
        await TransitionTestHelper.AdvertiseScreens(relay, "mac", 2);
        await TransitionTestHelper.AdvertiseScreens(relay, "win", 1);
        Assert.That(platform.IsOnVirtualScreen, Is.True, "pre-condition: on mac");
        platform.FireKeyEvent(Char(KeyEventType.KeyDown, 'a', KeyA));
        for (var i = 0; i < 20; i++)
            platform.FireMouseDelta(100, 0);
        Assert.That(relay.Snapshot().Any(s => s.Kind == MessageKind.EnterScreen && s.Targets.Contains("mac") && s.Json.Contains("mac:1")), Is.True,
            "pre-condition: on mac's second monitor");
        relay.ClearSent();

        platform.FireKeyEvent(Char(KeyEventType.KeyUp, 'a', KeyA));

        Assert.That(ForwardedKeys(relay, "mac").Select(k => (k.Type, k.Character)), Is.EqualTo([(KeyEventType.KeyUp, 'a')]));
        await Stop(new TestServiceBundle(platform, relay, service));
    }

    [Test]
    public async Task HeldThroughAReturnAndReEntryToTheSameHost_ItsRepeatsAndUpAreNotForwarded()
    {
        var bundle = await Started();
        Enter(bundle.Platform);
        bundle.Platform.FireKeyEvent(Char(KeyEventType.KeyDown, 'a', KeyA));
        bundle.Platform.FireMouseMove(1276, 720);
        Assert.That(bundle.Platform.IsOnVirtualScreen, Is.False, "pre-condition: returned");
        Enter(bundle.Platform);
        bundle.Relay.ClearSent();

        bundle.Platform.FireKeyEvent(Repeat('a', KeyA));
        bundle.Platform.FireKeyEvent(Char(KeyEventType.KeyUp, 'a', KeyA));

        Assert.That(ForwardedKeys(bundle.Relay), Is.Empty);
        await Stop(bundle);
    }

    [Test]
    public async Task HotkeyChord_ItsUnmatchedUpIsNotForwarded()
    {
        var bundle = await Started();
        Enter(bundle.Platform);
        bundle.Platform.FireKeyEvent(Char(KeyEventType.KeyDown, 'm', KeyM, Chord)); // relative mouse, consumed
        bundle.Relay.ClearSent();

        // the chord's modifiers went up first, so the 'm' up no longer matches the hotkey
        bundle.Platform.FireKeyEvent(Char(KeyEventType.KeyUp, 'm', KeyM));

        Assert.That(ForwardedKeys(bundle.Relay), Is.Empty);
        await Stop(bundle);
    }

    [Test]
    public async Task RemoteOnlyHotkeyEntry_TheChordsModifierUpsAreNotForwarded()
    {
        var (platform, relay, service) = TransitionTestHelper.CreateRemoteOnlyService();
        await service.StartAsync(CancellationToken.None);
        await TransitionTestHelper.BringHostOnline(relay, "mac");
        platform.FireKeyEvent(Char(KeyEventType.KeyDown, 'l', KeyL, Chord)); // unlock: local
        platform.FireKeyEvent(Char(KeyEventType.KeyUp, 'l', KeyL, Chord));
        Assert.That(platform.IsOnVirtualScreen, Is.False, "pre-condition: local");

        platform.FireKeyEvent(Char(KeyEventType.KeyDown, 'l', KeyL, Chord)); // relock: enters with the chord held
        Assert.That(platform.IsOnVirtualScreen, Is.True, "pre-condition: entered");
        relay.ClearSent();
        platform.FireKeyEvent(Char(KeyEventType.KeyUp, 'l', KeyL, Chord));
        platform.FireKeyEvent(Special(KeyEventType.KeyUp, SpecialKey.Super_L, SuperL, KeyModifiers.Control | KeyModifiers.Alt));
        platform.FireKeyEvent(Special(KeyEventType.KeyUp, SpecialKey.Alt_L, AltL, KeyModifiers.Control));
        platform.FireKeyEvent(Special(KeyEventType.KeyUp, SpecialKey.Control_L, CtrlL));

        Assert.That(ForwardedKeys(relay), Is.Empty);
        await Stop(new TestServiceBundle(platform, relay, service));
    }
}

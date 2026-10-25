using Hydra.Keyboard;
using Hydra.Relay;
using Tests.Setup;

namespace Tests.Screen;

// A slave that takes keypad keys says so in its ScreenInfo; one that predates them is sent what it understands.
[TestFixture]
public class KeypadForwardingTests : ServicesFixture
{
    private const int Keypad4 = 75;

    private readonly SlaveRelays _slaves = new();

    [TearDown]
    public Task StopSlaves() => _slaves.StopAll();

    private static KeyEvent Keypad(SpecialKey key, KeyModifiers mods = KeyModifiers.None) =>
        KeyEvent.Special(KeyEventType.KeyDown, key, mods) with { PhysicalKey = Keypad4 };

    private async Task<TestServiceBundle> Visiting(PeerPlatform local, bool slaveTakesKeypadKeys)
    {
        var bundle = await Services.Visiting(TransitionTestHelper.CreateService(localPlatform: local));
        await TransitionTestHelper.AdvertiseScreens(bundle.Relay, "remote", 1, keypadKeys: slaveTakesKeypadKeys);
        bundle.Relay.Sent.Clear();
        return bundle;
    }

    private static List<KeyEventMessage> Forwarded(FakeRelay relay) => relay.Sent.Of<KeyEventMessage>(MessageKind.KeyEvent, "remote");

    [Test]
    public async Task CurrentSlave_GetsTheKeypadKey()
    {
        var bundle = await Visiting(PeerPlatform.Linux, slaveTakesKeypadKeys: true);
        bundle.Platform.FireKeyEvent(Keypad(SpecialKey.KP_Left));
        Assert.That(Forwarded(bundle.Relay).Select(k => k.Key), Is.EqualTo([SpecialKey.KP_Left]));
    }

    [Test]
    public async Task OlderSlave_GetsKeypadNavigationAsTheDedicatedKey()
    {
        // it cannot read KP_Left and would drop it
        var bundle = await Visiting(PeerPlatform.Linux, slaveTakesKeypadKeys: false);
        bundle.Platform.FireKeyEvent(Keypad(SpecialKey.KP_Left));
        Assert.That(Forwarded(bundle.Relay).Select(k => k.Key), Is.EqualTo([SpecialKey.Left]));
    }

    [Test]
    public async Task OlderSlave_FromAMacMaster_GetsNoNumLock()
    {
        // it syncs NumLock on every key, so the numeric pad flag would flip it between keypad keys and the rest
        var bundle = await Visiting(PeerPlatform.MacOs, slaveTakesKeypadKeys: false);
        bundle.Platform.FireKeyEvent(Keypad(SpecialKey.KP_4, KeyModifiers.NumLock));
        Assert.That(Forwarded(bundle.Relay).Select(k => (k.Key, k.Modifiers)), Is.EqualTo([(SpecialKey.KP_4, KeyModifiers.None)]));
    }

    [Test]
    public async Task OlderSlave_FromALinuxMaster_KeepsNumLock()
    {
        // there it is the real lock state, which the older slave synced all along
        var bundle = await Visiting(PeerPlatform.Linux, slaveTakesKeypadKeys: false);
        bundle.Platform.FireKeyEvent(Keypad(SpecialKey.KP_4, KeyModifiers.NumLock));
        Assert.That(Forwarded(bundle.Relay).Select(k => k.Modifiers), Is.EqualTo([KeyModifiers.NumLock]));
    }

    [Test]
    public async Task Slave_AdvertisesKeypadKeys()
    {
        var slave = _slaves.Add(new TestableSlaveRelay());
        await slave.SimulateMasterConfig("master");
        Assert.That(slave.Sent.Of<ScreenInfoMessage>(MessageKind.ScreenInfo).Single().KeypadKeys, Is.True);
    }
}

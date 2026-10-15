using System.Text.Json;
using Cathedral.Config;
using Hydra.Relay;
using Hydra.Screen;
using Microsoft.Extensions.Logging;
using Tests.Setup;

namespace Tests.Screen;

// A screen's name is its id across the whole layout, so the master keeps only the names a peer can own.
[TestFixture]
public class ScreenNamingTests
{
    [TestCase("mac", "mac")]
    [TestCase("mac", "mac:0")]
    [TestCase("mac", "mac:12")]
    [TestCase("mac", "MAC:1")]
    [TestCase("desktop-pc", "DESKTOP-PC")]
    public void Owns_ItsOwnNames(string host, string name) =>
        Assert.That(ScreenNaming.Owns(host, name), Is.True);

    [TestCase("mac", "win")]
    [TestCase("mac", "win:0")]
    [TestCase("mac", "macx")]
    [TestCase("mac", "mac:")]
    [TestCase("mac", "mac:x")]
    [TestCase("mac", "mac:1x")]
    [TestCase("mac", "mac:-1")]
    [TestCase("mac", "mac:+1")]
    [TestCase("mac", "mac: 1")]
    [TestCase("mac", "mac:1 ")]
    [TestCase("mac", "mac:１")]
    [TestCase("a", "ab:0")]
    [TestCase("a", "a:1:0")]
    public void Owns_NothingElse(string host, string name) =>
        Assert.That(ScreenNaming.Owns(host, name), Is.False);

    [Test]
    public void OwnedBy_RefusesForeignNamesAndRepeats_KeepingTheFirst()
    {
        var owned = ScreenNaming.OwnedBy("remote", [Entry("remote:0", 0), Entry("remote2", 1), Entry("REMOTE:0", 2), Entry("Remote:1", 3)]);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(owned.Kept.Select(e => (e.Name, e.X)), Is.EqualTo([("remote:0", 0), ("Remote:1", 3)]));
            Assert.That(owned.Refused, Is.EqualTo(["remote2", "REMOTE:0"]));
        }
    }

    [Test]
    public void OwnedBy_AcceptsUppercaseNamesFromALowercaseHost()
    {
        // Styx lowercases the hostname; a Windows slave names its screens from its uppercase machine name
        var owned = ScreenNaming.OwnedBy("desktop-pc", [Entry("DESKTOP-PC:0", 0), Entry("DESKTOP-PC:1", 1)]);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(owned.Refused, Is.Empty);
            Assert.That(owned.Kept, Has.Count.EqualTo(2));
        }
    }

    // -- the master's handler --

    [Test]
    public async Task ForeignName_IsIgnoredAndLogged_AndTheCursorStaysWhereItIs()
    {
        var log = new CapturingLog<InputRouter>(LogLevel.Warning);
        var world = new WorldState();
        var ticks = new SteppableTicks();
        var bundle = TransitionTestHelper.CreateService(profile: TransitionTestHelper.ChainConfig, world: world, log: log, getTickCount: ticks.Now);
        await bundle.Service.StartAsync(CancellationToken.None);
        await TransitionTestHelper.BringHostsOnline(bundle.Relay, ["remote", "remote2"]);
        bundle.Platform.FireMouseMove(2559, 720);
        for (var i = 0; i < 30; i++)
            bundle.Platform.FireMouseDelta(100, 0);
        Assert.That(bundle.Relay.Snapshot().Any(s => s.Kind == MessageKind.EnterScreen && s.Targets.Contains("remote2")), Is.True,
            "pre-condition: on remote2");
        bundle.Relay.ClearSent();

        // remote, earlier in the layout than remote2, claims the screen the cursor is on
        await Advertise(bundle.Relay, "remote", Entry("remote", 0), Entry("remote2", 2560));
        ticks.Advance(20); // past the mouse throttle
        bundle.Platform.FireMouseDelta(-10, 0);

        var sent = bundle.Relay.Snapshot();
        var peerScreens = await world.GetPeerScreensSnapshot();
        await TransitionTestHelper.Stop(bundle);
        using (Assert.EnterMultipleScope())
        {
            Assert.That(peerScreens["remote"].Select(e => e.Name), Is.EqualTo(["remote"]), "the foreign entry never reaches the world");
            Assert.That(sent.Where(s => s.Kind is MessageKind.MouseMove or MessageKind.MouseMoveDelta).SelectMany(s => s.Targets), Is.Not.Empty.And.All.EqualTo("remote2"),
                "the cursor stays on remote2");
            Assert.That(sent.Any(s => s.Kind is MessageKind.EnterScreen or MessageKind.LeaveScreen), Is.False, "no host change");
            Assert.That(log.Messages, Is.EqualTo(["remote sent screen remote2, which are not its own or repeat one it already sent; ignored"]));
        }
    }

    [Test]
    public async Task RepeatedNameWithinOnePeer_IsDropped_AndTheCursorStillEnters()
    {
        var world = new WorldState();
        var bundle = TransitionTestHelper.CreateService(world: world);
        await bundle.Service.StartAsync(CancellationToken.None);
        await bundle.Relay.FirePeersChanged("remote");
        await Advertise(bundle.Relay, "remote", Entry("remote:0", 0), Entry("REMOTE:0", 2560));

        bundle.Platform.FireMouseMove(2559, 720);

        var peerScreens = await world.GetPeerScreensSnapshot();
        var entered = bundle.Platform.IsOnVirtualScreen;
        await TransitionTestHelper.Stop(bundle);
        using (Assert.EnterMultipleScope())
        {
            Assert.That(peerScreens["remote"].Select(e => e.Name), Is.EqualTo(["remote:0"]));
            Assert.That(entered, Is.True, "entering a host must not throw over a repeated name");
        }
    }

    [Test]
    public async Task OnlyForeignNames_AreHandledLikeNoScreens_KeepingWhatThePeerHad()
    {
        var world = new WorldState();
        var bundle = TransitionTestHelper.CreateService(world: world);
        await bundle.Service.StartAsync(CancellationToken.None);
        await TransitionTestHelper.BringHostOnline(bundle.Relay, "remote");

        await Advertise(bundle.Relay, "remote", Entry("home:0", 0));

        var peerScreens = await world.GetPeerScreensSnapshot();
        await TransitionTestHelper.Stop(bundle);
        Assert.That(peerScreens["remote"].Select(e => e.Name), Is.EqualTo(["remote"]));
    }

    private static ScreenInfoEntry Entry(string name, int x) => new(name, x, 0, 2560, 1440, 1.0m);

    private static async Task Advertise(FakeRelay relay, string host, params ScreenInfoEntry[] entries) =>
        await relay.FireMessageReceived(host, MessageKind.ScreenInfo, JsonSerializer.Serialize(new ScreenInfoMessage([.. entries]), SaneJson.Options));
}

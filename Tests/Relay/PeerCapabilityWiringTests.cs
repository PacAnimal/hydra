using System.Text.Json;
using Cathedral.Config;
using Hydra.Relay;
using Hydra.Screen;
using Tests.Setup;

namespace Tests.Relay;

/// <summary>
/// What a peer advertises on the wire reaches the master's world — the single link that joins the two
/// halves of key bundling.
///
/// <para><b>This fixture exists because everything on either side of that link was tested and the link
/// itself was not.</b> A slave putting its capabilities on <c>ScreenInfo</c> has a test; <c>WorldState</c>
/// remembering them has a test; <c>RelayConnection</c> bundling once told they are supported has a test —
/// and all three reach PAST the wire and set the capabilities by hand, because that is the only way to
/// arrange the state each of them is about. Nothing drove the handler that does it for real.</para>
///
/// <para>Measured: deleting <c>InputRouter</c>'s one <c>SetPeerCapabilities</c> call left <b>603</b> tests
/// passing. In the field that is <c>PeerSupports</c> false for every peer for ever, so
/// <c>EveryTargetSupports</c> answers false to everything and not one key is bundled — the whole feature
/// inert, silently, behind a green suite.</para>
/// </summary>
[TestFixture]
public class PeerCapabilityWiringTests
{
    private FakePlatform _platform = null!;
    private FakeRelay _relay = null!;
    private InputRouter _service = null!;
    private WorldState _world = null!;

    [SetUp]
    public async Task SetUp()
    {
        _world = new WorldState();
        (_platform, _relay, _service) = TransitionTestHelper.CreateService(world: _world);
        await _service.StartAsync(CancellationToken.None);
        await _relay.FirePeersChanged("remote");
    }

    [TearDown]
    public async Task TearDown()
    {
        await _service.StopAsync(CancellationToken.None);
        await _platform.DisposeAsync();
    }

    /// <summary>Screens are required for the handler to act, so every case here carries one.</summary>
    private async Task Advertise(string[]? capabilities) =>
        await _relay.FireMessageReceived("remote", MessageKind.ScreenInfo,
            JsonSerializer.Serialize(new ScreenInfoMessage([new ScreenInfoEntry("screen:0", 0, 0, 2560, 1440, 1.0m)], Capabilities: capabilities), SaneJson.Options));

    [Test]
    public async Task WhatAPeerAdvertisesIsWhatTheMasterRecords()
    {
        await Advertise(PeerCapabilities.Advertise());

        Assert.That(_world.PeerSupports("remote", PeerCapability.KeyEventBatch), Is.True,
            "the master never recorded what the peer said it could do, so it will never bundle a key for it");
    }

    /// <summary>
    /// The negative control. Without it the test above is satisfied by a master that assumes every peer is
    /// capable — which is the dangerous direction, since an unrecognised kind is discarded in silence.
    /// </summary>
    [Test]
    public async Task APeerThatAdvertisesNothingIsTreatedAsCapableOfNothing()
    {
        await Advertise(null);

        Assert.That(_world.PeerSupports("remote", PeerCapability.KeyEventBatch), Is.False,
            "a peer that advertised nothing was treated as capable, so it will be sent a frame it discards without a word");
    }

    /// <summary>
    /// An advertisement REPLACES. A peer that drops and comes back on an older build must take its
    /// capabilities away with it, or the master goes on bundling to a build that cannot read a bundle.
    /// </summary>
    [Test]
    public async Task ReconnectingOnAnOlderBuildTakesTheCapabilityAway()
    {
        await Advertise(PeerCapabilities.Advertise());
        Assert.That(_world.PeerSupports("remote", PeerCapability.KeyEventBatch), Is.True, "precondition: the capability was recorded");

        await Advertise(null);

        Assert.That(_world.PeerSupports("remote", PeerCapability.KeyEventBatch), Is.False,
            "the master kept a capability the peer has stopped claiming — every key sent to it is now discarded in silence");
    }

    /// <summary>A name this build does not know is ignored, and does not cost the peer the ones it does know.</summary>
    [Test]
    public async Task AnUnknownNameIsIgnoredWithoutCostingTheKnownOnes()
    {
        await Advertise([.. PeerCapabilities.Advertise(), "SomethingFromALaterBuild"]);

        Assert.That(_world.PeerSupports("remote", PeerCapability.KeyEventBatch), Is.True,
            "an unknown capability name threw away the whole advertisement");
    }
}

using Hydra.Relay;

namespace Tests.Relay;

/// <summary>
/// A peer's capabilities live exactly as long as the peer does.
///
/// <para><b>It is a claim by a running build, not a property of a hostname.</b> The same machine can come
/// back on an older build — a downgrade, a rollback, a second machine taking the name — and if it inherits
/// the previous occupant's claim the master will bundle key events at something that discards them in
/// silence. The peer's next <c>ScreenInfo</c> does correct it, but there is a window before that arrives,
/// and the entire reason the master asks before bundling is that being wrong costs keystrokes rather than
/// raising an error.</para>
/// </summary>
[TestFixture]
public class PeerCapabilityLifetimeTests
{
    private const string Slave = "slave-pc";

    private static HashSet<string> Hosts(params string[] hosts) => new(hosts, StringComparer.OrdinalIgnoreCase);

    [Test]
    public void APeerThatNeverSpokeSupportsNothing()
    {
        // The default that makes every other case safe: silence is NO, so a master that has heard nothing
        // sends one KeyEvent per frame.
        Assert.That(new WorldState().PeerSupports(Slave, PeerCapability.KeyEventBatch), Is.False);
    }

    [Test]
    public async Task ADepartedPeerTakesItsCapabilityWithIt()
    {
        var world = new WorldState();
        await world.UpdatePeers(Hosts(Slave), Hosts(Slave));
        world.SetPeerCapabilities(Slave, PeerCapabilities.Parse([nameof(PeerCapability.KeyEventBatch)]));

        Assert.That(world.PeerSupports(Slave, PeerCapability.KeyEventBatch), Is.True, "the premise — it advertised while it was here");

        // Gone from the relay's peer list.
        await world.UpdatePeers(Hosts(), Hosts(Slave));

        Assert.That(world.PeerSupports(Slave, PeerCapability.KeyEventBatch), Is.False,
            "the capability outlived the peer — the same name returning on an older build would be sent bundles it drops");
    }

    [Test]
    public async Task AFullPeerResetClearsItToo()
    {
        var world = new WorldState();
        world.SetPeerCapabilities(Slave, PeerCapabilities.Parse([nameof(PeerCapability.KeyEventBatch)]));

        await world.ClearPeers();

        Assert.That(world.PeerSupports(Slave, PeerCapability.KeyEventBatch), Is.False, "a relay reconnect re-learns every peer from scratch");
    }

    /// <summary>
    /// A peer that comes back is believed again once it says so — the clearing above must not leave the
    /// capability permanently off, or bundling would work exactly once per process.
    /// </summary>
    [Test]
    public async Task APeerThatReturnsAndReadvertisesIsBelievedAgain()
    {
        var world = new WorldState();
        await world.UpdatePeers(Hosts(Slave), Hosts(Slave));
        world.SetPeerCapabilities(Slave, PeerCapabilities.Parse([nameof(PeerCapability.KeyEventBatch)]));
        await world.UpdatePeers(Hosts(), Hosts(Slave));

        await world.UpdatePeers(Hosts(Slave), Hosts(Slave));
        world.SetPeerCapabilities(Slave, PeerCapabilities.Parse([nameof(PeerCapability.KeyEventBatch)]));

        Assert.That(world.PeerSupports(Slave, PeerCapability.KeyEventBatch), Is.True);
    }

    /// <summary>
    /// And a peer that returns on a build which says nothing is taken at its word: the master records what
    /// every <c>ScreenInfo</c> says, including one that omits the flag.
    /// </summary>
    [Test]
    public async Task APeerThatReturnsWithoutAdvertisingStaysOff()
    {
        var world = new WorldState();
        await world.UpdatePeers(Hosts(Slave), Hosts(Slave));
        world.SetPeerCapabilities(Slave, PeerCapabilities.Parse([nameof(PeerCapability.KeyEventBatch)]));
        await world.UpdatePeers(Hosts(), Hosts(Slave));

        await world.UpdatePeers(Hosts(Slave), Hosts(Slave));
        world.SetPeerCapabilities(Slave, PeerCapabilities.Parse(null)); // what InputRouter records for a ScreenInfo advertising nothing

        Assert.That(world.PeerSupports(Slave, PeerCapability.KeyEventBatch), Is.False);
    }
}

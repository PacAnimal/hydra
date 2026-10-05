using Hydra.Config;
using Hydra.Management;
using Hydra.Relay;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Tests.Setup;

namespace Tests.Management;

public class HydraStatusServiceTests
{
    [Test]
    public async Task PeersCarryTheirPlatformsDisplayName()
    {
        var path = Path.Combine(TestPaths.FreshFixtureRoot(nameof(HydraStatusServiceTests)), "hydra.conf");
        await File.WriteAllTextAsync(path, """{ "name": "home", "profiles": [{ "mode": "Master" }] }""");
        var runtime = new HydraRuntimeInfo(path, DateTimeOffset.UtcNow);
        var world = new WorldState();
        await world.UpdatePeers(["laptop"], ["laptop"]);
        await world.SetPeerPlatform("laptop", PeerPlatform.MacOs);
        await using var services = new ServiceCollection().BuildServiceProvider();
        var status = new HydraStatusService(services, TransitionTestHelper.TestConfig, world,
            new DormancyState(NullLogger<DormancyState>.Instance), runtime, new TransactionalConfigStore(runtime));

        var snapshot = await status.GetAsync(CancellationToken.None);

        Assert.That(snapshot.Peers.Single().Platform, Is.EqualTo("macOS"));
    }
}

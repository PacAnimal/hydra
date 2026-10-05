using Hydra.Management;
using Microsoft.Extensions.Logging.Abstractions;
using Tests.Setup;

namespace Tests.Management;

public class ManagementServerTests
{
    private string _configPath = null!;

    [SetUp]
    public void SetUp() => _configPath = Path.Combine(TestPaths.FreshFixtureRoot(nameof(ManagementServerTests)), "hydra.conf");

    [TestCase(true, "Hydra shutdown requested.")]
    [TestCase(false, "Shutdown is unavailable.")]
    public async Task ShutdownDispatchReturnsTheLifetimeDecision(bool accepted, string message)
    {
        var lifetime = new FakeLifetimeController(new CommandResult(accepted, message));
        var server = new ManagementServer(
            null!,
            new HydraRuntimeInfo(_configPath, DateTimeOffset.UtcNow),
            null!, null!, null!, lifetime, null!, NullLogger<ManagementServer>.Instance);

        var response = await server.DispatchAsync(new ManagementRequest(ManagementMethods.HydraShutdown), CancellationToken.None);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(response.Success, Is.True);
            Assert.That(ManagementJson.Deserialize<CommandResult>(response.Json),
                Is.EqualTo(new CommandResult(accepted, message)));
            Assert.That(lifetime.ShutdownRequests, Is.EqualTo(1));
        }
    }

    // the entry assembly is the test host here, and a published single-file app's is Hydra by luck
    [Test]
    public async Task HelloReportsHydrasOwnVersion()
    {
        var server = new ManagementServer(
            null!,
            new HydraRuntimeInfo(_configPath, DateTimeOffset.UtcNow),
            null!, null!, null!, new FakeLifetimeController(), null!, NullLogger<ManagementServer>.Instance);

        var response = await server.DispatchAsync(new ManagementRequest(ManagementMethods.Hello), CancellationToken.None);

        Assert.That(ManagementJson.Deserialize<ServerHello>(response.Json).HydraVersion,
            Is.EqualTo(typeof(ManagementServer).Assembly.GetName().Version!.ToString(3)));
    }
}

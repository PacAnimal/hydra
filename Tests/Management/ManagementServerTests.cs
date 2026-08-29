using Hydra.Management;
using Microsoft.Extensions.Logging.Abstractions;

namespace Tests.Management;

public class ManagementServerTests
{
    [TestCase(true, "Hydra shutdown requested.")]
    [TestCase(false, "Shutdown is unavailable.")]
    public async Task ShutdownDispatchReturnsTheLifetimeDecision(bool accepted, string message)
    {
        var lifetime = new FakeLifetime(new CommandResult(accepted, message));
        var server = new ManagementServer(
            new HydraRuntimeInfo(Path.Combine(TestContext.CurrentContext.WorkDirectory,
                $"server-{Guid.NewGuid():N}.conf"), DateTimeOffset.UtcNow),
            null!, null!, null!, lifetime, null!, NullLogger<ManagementServer>.Instance);

        var response = await server.DispatchAsync(new ManagementRequest("hydra.shutdown"), CancellationToken.None);

        Assert.Multiple(() =>
        {
            Assert.That(response.Success, Is.True);
            Assert.That(ManagementJson.Deserialize<CommandResult>(response.Json),
                Is.EqualTo(new CommandResult(accepted, message)));
            Assert.That(lifetime.ShutdownRequests, Is.EqualTo(1));
        });
    }

    private sealed class FakeLifetime(CommandResult shutdownResult) : IHydraLifetimeController
    {
        internal int ShutdownRequests { get; private set; }

        public void RestartAfterResponse() { }

        public CommandResult ShutdownAfterResponse()
        {
            ShutdownRequests++;
            return shutdownResult;
        }
    }
}

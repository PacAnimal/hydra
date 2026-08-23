using Microsoft.Extensions.Logging.Abstractions;
using Styx.Services;

namespace Tests.Styx;

[TestFixture]
public class ClientRegistryTests
{
    [Test]
    public async Task HostLookup_IsCaseInsensitiveAndNetworkScoped()
    {
        var registry = new ClientRegistry(NullLogger<ClientRegistry>.Instance);
        var networkA = Guid.NewGuid();
        var networkB = Guid.NewGuid();
        await registry.Register("a", networkA, "Workstation", "10.0.0.1");
        await registry.Register("b", networkB, "Workstation", "10.0.0.2");

        using (Assert.EnterMultipleScope())
        {
            Assert.That(await registry.GetConnectionId(networkA, "WORKSTATION"), Is.EqualTo("a"));
            Assert.That(await registry.GetConnectionId(networkB, "workstation"), Is.EqualTo("b"));
            Assert.That((await registry.GetIdentity("a"))?.HostName, Is.EqualTo("Workstation"));
        }
    }

    [Test]
    public async Task ReRegisteringConnection_RemovesPreviousHostIndex()
    {
        var registry = new ClientRegistry(NullLogger<ClientRegistry>.Instance);
        var network = Guid.NewGuid();
        await registry.Register("connection", network, "old", "10.0.0.1");
        await registry.Register("connection", network, "new", "10.0.0.1");

        using (Assert.EnterMultipleScope())
        {
            Assert.That(await registry.GetConnectionId(network, "old"), Is.Null);
            Assert.That(await registry.GetConnectionId(network, "new"), Is.EqualTo("connection"));
        }
    }

    [Test]
    public async Task Unregister_RemovesBothIndexes()
    {
        var registry = new ClientRegistry(NullLogger<ClientRegistry>.Instance);
        var network = Guid.NewGuid();
        await registry.Register("connection", network, "host", "10.0.0.1");

        await registry.Unregister("connection");

        using (Assert.EnterMultipleScope())
        {
            Assert.That(await registry.GetConnectionId(network, "host"), Is.Null);
            Assert.That(await registry.GetIdentity("connection"), Is.Null);
        }
    }
}

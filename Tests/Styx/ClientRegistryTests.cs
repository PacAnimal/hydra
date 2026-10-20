using Microsoft.Extensions.Logging.Abstractions;
using Styx.Services;

namespace Tests.Styx;

[TestFixture]
public class ClientRegistryTests
{
    private const string LocalIp = "192.168.1.1";

    [Test]
    public void HostLookup_IsCaseInsensitiveAndNetworkScoped()
    {
        var registry = new ClientRegistry(NullLogger<ClientRegistry>.Instance);
        var networkA = Guid.NewGuid();
        var networkB = Guid.NewGuid();
        registry.Register("a", networkA, "Workstation", "10.0.0.1", "192.168.1.10");
        registry.Register("b", networkB, "Workstation", "10.0.0.2", LocalIp);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(registry.GetConnectionId(networkA, "WORKSTATION"), Is.EqualTo("a"));
            Assert.That(registry.GetConnectionId(networkB, "workstation"), Is.EqualTo("b"));
            Assert.That(registry.GetIdentity("a")?.HostName, Is.EqualTo("Workstation"));
            Assert.That(registry.GetIdentity("a")?.LocalIp, Is.EqualTo("192.168.1.10"));
            Assert.That(registry.GetAllIdentities(), Has.Count.EqualTo(2));
        }
    }

    [Test]
    public void ReRegisteringConnection_RemovesPreviousHostIndex()
    {
        var registry = new ClientRegistry(NullLogger<ClientRegistry>.Instance);
        var network = Guid.NewGuid();
        registry.Register("connection", network, "old", "10.0.0.1", LocalIp);
        registry.Register("connection", network, "new", "10.0.0.1", LocalIp);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(registry.GetConnectionId(network, "old"), Is.Null);
            Assert.That(registry.GetConnectionId(network, "new"), Is.EqualTo("connection"));
        }
    }

    [Test]
    public void Unregister_RemovesBothIndexes()
    {
        var registry = new ClientRegistry(NullLogger<ClientRegistry>.Instance);
        var network = Guid.NewGuid();
        registry.Register("connection", network, "host", "10.0.0.1", LocalIp);

        registry.Unregister("connection");

        using (Assert.EnterMultipleScope())
        {
            Assert.That(registry.GetConnectionId(network, "host"), Is.Null);
            Assert.That(registry.GetIdentity("connection"), Is.Null);
        }
    }

    [Test]
    public void Register_MatchesExistingHostNameCaseInsensitively()
    {
        var registry = new ClientRegistry(NullLogger<ClientRegistry>.Instance);
        var network = Guid.NewGuid();
        registry.Register("a", network, "Laptop", "10.0.0.1", LocalIp);

        var result = registry.Register("b", network, "LAPTOP", "10.0.0.2", LocalIp);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(result.Kicked, Is.EqualTo("a"),
                "the displaced connection must be reported as kicked, not silently dropped");
            Assert.That(registry.GetIdentity("a"), Is.Null);
            Assert.That(registry.GetConnectionId(network, "laptop"), Is.EqualTo("b"));
        }
    }

    [Test]
    public async Task ConcurrentDuplicateRegistrations_LeaveOneConsistentEntry()
    {
        var registry = new ClientRegistry(NullLogger<ClientRegistry>.Instance);
        var network = Guid.NewGuid();

        await Task.WhenAll(Enumerable.Range(0, 64).Select(i => Task.Run(() =>
            registry.Register($"connection-{i}", network, "shared-host", $"10.0.0.{i}", LocalIp))));

        var clients = registry.GetNetworkClients(network);
        using (Assert.EnterMultipleScope())
        {
            Assert.That(clients, Has.Count.EqualTo(1));
            Assert.That(registry.GetConnectionId(network, "SHARED-HOST"), Is.EqualTo(clients[0].ConnectionId));
            Assert.That(registry.GetIdentity(clients[0].ConnectionId)?.HostName, Is.EqualTo("shared-host"));
        }
    }

    [Test]
    public void EveryRegistration_GetsItsOwnGeneration()
    {
        var registry = new ClientRegistry(NullLogger<ClientRegistry>.Instance);
        var network = Guid.NewGuid();
        var first = registry.Register("connection", network, "host", "10.0.0.1", LocalIp);
        var second = registry.Register("connection", network, "host", "10.0.0.1", LocalIp);
        Assert.That(second.Generation, Is.GreaterThan(first.Generation));
    }

    [Test]
    public void UnregisterIfCurrent_LeavesALaterRegistrationAlone()
    {
        var registry = new ClientRegistry(NullLogger<ClientRegistry>.Instance);
        var network = Guid.NewGuid();
        var stale = registry.Register("connection", network, "host", "10.0.0.1", LocalIp);
        var current = registry.Register("connection", network, "host", "10.0.0.1", LocalIp);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(registry.UnregisterIfCurrent("connection", stale.Generation), Is.Null);
            Assert.That(registry.GetIdentity("connection"), Is.Not.Null);
            Assert.That(registry.UnregisterIfCurrent("connection", current.Generation), Is.Not.Null);
            Assert.That(registry.GetIdentity("connection"), Is.Null);
        }
    }

    [Test]
    public void ReRegisteringOnAnotherPath_ReportsWhereItLeft()
    {
        var registry = new ClientRegistry(NullLogger<ClientRegistry>.Instance);
        var network = Guid.NewGuid();
        var lobby = new NetworkKey(network, "");
        var room = new NetworkKey(network, "room");
        var first = registry.Register("connection", lobby, "host", "10.0.0.1", LocalIp);
        var moved = registry.Register("connection", room, "host", "10.0.0.1", LocalIp);
        var stayed = registry.Register("connection", room, "renamed", "10.0.0.1", LocalIp);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(first.Left, Is.Null);
            Assert.That(moved.Left, Is.EqualTo(lobby));
            Assert.That(stayed.Left, Is.Null);
            Assert.That(registry.GetNetworkClients(lobby), Is.Empty);
        }
    }

    [Test]
    public void PathsAreSeparateNamespaces()
    {
        var registry = new ClientRegistry(NullLogger<ClientRegistry>.Instance);
        var network = Guid.NewGuid();
        registry.Register("a", new NetworkKey(network, "x"), "host", "10.0.0.1", LocalIp);
        registry.Register("b", new NetworkKey(network, "y"), "host", "10.0.0.2", LocalIp);
        using (Assert.EnterMultipleScope())
        {
            Assert.That(registry.GetConnectionId(new NetworkKey(network, "x"), "host"), Is.EqualTo("a"));
            Assert.That(registry.GetConnectionId(new NetworkKey(network, "y"), "HOST"), Is.EqualTo("b"));
            Assert.That(registry.GetConnectionId(network, "host"), Is.Null);
        }
    }
}

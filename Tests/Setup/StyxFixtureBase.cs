using Cathedral.Styx;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;

namespace Tests.Setup;

/// <summary>A fixture with its own in-memory Styx server, up before the first test.</summary>
public abstract class StyxFixtureBase
{
    private WebApplicationFactory<global::Styx.Program>? _factory;

    protected WebApplicationFactory<global::Styx.Program> Factory =>
        _factory ?? throw new InvalidOperationException("the Styx server starts in OneTimeSetUp");

    [OneTimeSetUp]
    public void StartStyx()
    {
        _factory = StyxTestServer.Create(configure: ConfigureRelay);
        _ = _factory.Server; // eager init — avoid paying startup cost inside a test
    }

    // services this fixture's relay adds or replaces, before it starts
    protected virtual void ConfigureRelay(IServiceCollection services) { }

    [OneTimeTearDown]
    public async Task StopStyx()
    {
        if (_factory != null) await _factory.DisposeAsync();
    }

    protected Task<ClientPair> ConnectedPair(string sender = "sender", string receiver = "receiver") =>
        StyxTestServer.ConnectedPair(Factory, sender, receiver);

    protected Task<PeerClients> ConnectedPeers(params string[] names) => ConnectedPeers(_ => global::Styx.Constants.RelayPath, names);

    /// <summary>
    /// Raw clients on a fresh network, each on the path <paramref name="pathOf"/> gives its name, authenticated and having
    /// seen all the others. A setup that fails partway disposes every one.
    /// </summary>
    protected async Task<PeerClients> ConnectedPeers(Func<string, string> pathOf, params string[] names)
    {
        var network = StyxToken.NewNetworkId();
        var token = StyxTestServer.MintToken(network).ToString();
        var peers = new PeerClients(network, names.ToDictionary(name => name, _ => new TestStyxClient()));
        try
        {
            foreach (var (name, client) in peers.Clients)
                Assert.That((await client.Connect(Factory, token, name, pathOf(name))).Authenticated, Is.True);
            foreach (var (name, client) in peers.Clients)
                await client.WaitForPeers([.. names.Where(other => other != name)], anyOrder: true);
            return peers;
        }
        catch
        {
            await peers.DisposeAsync();
            throw;
        }
    }
}

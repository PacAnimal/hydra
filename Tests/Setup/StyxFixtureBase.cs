using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;

namespace Tests.Setup;

/// <summary>A fixture with its own in-memory Styx server, up before the first test.</summary>
public abstract class StyxFixtureBase
{
    /// <summary>Everything here is bounded. A test that can hang takes the run with it.</summary>
    protected static readonly TimeSpan Bound = TimeSpan.FromSeconds(30);

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

    protected Task<ClientPair> ConnectedPair(string sender = "sender", string receiver = "receiver", bool? peerTakesBundles = null) =>
        StyxTestServer.ConnectedPair(Factory, sender, receiver, peerTakesBundles);
}

namespace Tests.Setup;

/// <summary>Raw relay clients on one network, by host name, disposed together.</summary>
public sealed record PeerClients(Guid Network, Dictionary<string, TestStyxClient> Clients) : IAsyncDisposable
{
    public TestStyxClient this[string name] => Clients[name];

    public async ValueTask DisposeAsync()
    {
        foreach (var client in Clients.Values) await client.DisposeAsync();
    }
}

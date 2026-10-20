namespace Tests.Setup;

/// <summary>Raw relay clients on one network, by host name, disposed together.</summary>
public sealed record PeerClients(Guid Network, Dictionary<string, TestStyxClient> Clients) : IAsyncDisposable
{
    public TestStyxClient this[string name] => Clients[name];

    // every client, even when an earlier one's disposal throws
    public async ValueTask DisposeAsync()
    {
        List<Exception> failures = [];
        foreach (var client in Clients.Values)
        {
            try { await client.DisposeAsync(); }
            catch (Exception ex) { failures.Add(ex); }
        }
        if (failures.Count > 0) throw new AggregateException(failures);
    }
}

namespace Tests.Setup;

/// <summary>Two relay clients that are both connected, disposed together.</summary>
public sealed class ClientPair(HydraTestClient sender, HydraTestClient receiver) : IAsyncDisposable
{
    public HydraTestClient Sender { get; } = sender;
    public HydraTestClient Receiver { get; } = receiver;

    /// <summary>
    /// Starts both and waits until both have authenticated. A setup that fails partway disposes BOTH, so a
    /// client that did connect is not left running until the fixture ends.
    /// </summary>
    public static async Task<ClientPair> Start(HydraTestClient sender, HydraTestClient receiver, CancellationToken cancel = default)
    {
        var pair = new ClientPair(sender, receiver);
        try
        {
            await sender.StartReady(cancel);
            await receiver.StartReady(cancel);
            return pair;
        }
        catch
        {
            await pair.DisposeAsync();
            throw;
        }
    }

    public void Deconstruct(out HydraTestClient sender, out HydraTestClient receiver)
    {
        sender = Sender;
        receiver = Receiver;
    }

    public async ValueTask DisposeAsync()
    {
        try { await Sender.DisposeAsync(); }
        finally { await Receiver.DisposeAsync(); }
    }
}

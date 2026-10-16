using Cathedral.Styx;
using Common.Interfaces;
using Microsoft.AspNetCore.SignalR;
using Microsoft.AspNetCore.SignalR.Client;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Time.Testing;
using Styx;
using Styx.Services;
using Tests.Setup;

namespace Tests.Styx;

/// <summary>
/// A peer whose download has stalled while its pings still arrive never trips the client timeout, so the relay's write to
/// it is bounded by <see cref="PeerWriter.Deadline"/> instead. Writes to the stalled peer park in its lifetime manager as
/// they would on a full transport, and the deadline runs on a fake clock, so nothing here is timed.
/// </summary>
[TestFixture]
public class StalledPeerTests : StyxFixtureBase
{
    private readonly FakeTimeProvider _clock = new();
    private StallingLifetimeManager Writes => (StallingLifetimeManager)Factory.Services.GetRequiredService<HubLifetimeManager<StyxHub>>();

    protected override void ConfigureRelay(IServiceCollection services)
    {
        services.AddSingleton<TimeProvider>(_clock);
        services.AddSingleton<DefaultHubLifetimeManager<StyxHub>>();
        services.AddSingleton<HubLifetimeManager<StyxHub>>(provider => new StallingLifetimeManager(provider.GetRequiredService<DefaultHubLifetimeManager<StyxHub>>(), provider));
    }

    [TearDown]
    public void ReleaseEverything() => Writes.Release();

    [Test]
    public async Task APeerThatTakesNothingWithinTheDeadline_IsDropped_AndTheSenderMovesOn()
    {
        await using var peers = await ConnectedPeers();
        await Writes.Stall(peers.Network, "stalled");

        var parked = peers.Sender.Server!.Send(["stalled"], [1]);
        await Writes.ParkedReceive();
        _clock.Advance(PeerWriter.Deadline);

        await parked.WaitAsync(Bound);
        await peers.Stalled.WaitForClose();
        await peers.Sender.Server!.Send(["receiver"], [2]).WaitAsync(Bound);
        Assert.That(await peers.Receiver.NextReceived(), Is.EqualTo(new byte[] { 2 }));
    }

    [Test]
    public async Task APeerThatTakesTheMessageWithinTheDeadline_StaysConnected()
    {
        await using var peers = await ConnectedPeers();
        await Writes.Stall(peers.Network, "stalled");

        var parked = peers.Sender.Server!.Send(["stalled"], [1]);
        await Writes.ParkedReceive();
        _clock.Advance(PeerWriter.Deadline - TimeSpan.FromTicks(1));
        Writes.Release();

        await parked.WaitAsync(Bound);
        Assert.That(await peers.Stalled.NextReceived(), Is.EqualTo(new byte[] { 1 }));
        await peers.Sender.Server!.Send(["stalled"], [2]).WaitAsync(Bound);
        using (Assert.EnterMultipleScope())
        {
            Assert.That(await peers.Stalled.NextReceived(), Is.EqualTo(new byte[] { 2 }));
            Assert.That(peers.Stalled.State, Is.EqualTo(HubConnectionState.Connected));
        }
    }

    // three peers on a fresh network, each having seen the other two, so no membership write is still on its way
    private async Task<Peers> ConnectedPeers()
    {
        var network = StyxToken.NewNetworkId();
        var token = StyxTestServer.MintToken(network).ToString();
        var peers = new Peers(network, new TestStyxClient(), new TestStyxClient(), new TestStyxClient());
        try
        {
            foreach (var (client, name) in peers.Named)
                Assert.That((await client.Connect(Factory, token, name)).Authenticated, Is.True);
            foreach (var (client, _) in peers.Named)
                while ((await client.WaitForPeers()).Length < 2) { }
            return peers;
        }
        catch
        {
            await peers.DisposeAsync();
            throw;
        }
    }

    private sealed record Peers(Guid Network, TestStyxClient Sender, TestStyxClient Receiver, TestStyxClient Stalled) : IAsyncDisposable
    {
        public IEnumerable<(TestStyxClient Client, string Name)> Named => [(Sender, "sender"), (Receiver, "receiver"), (Stalled, "stalled")];

        public async ValueTask DisposeAsync()
        {
            foreach (var (client, _) in Named) await client.DisposeAsync();
        }
    }

    /// <summary>The relay's own lifetime manager, parking every write to one connection until released or cancelled.</summary>
    private sealed class StallingLifetimeManager(DefaultHubLifetimeManager<StyxHub> inner, IServiceProvider services)
        : SharedHubLifetimeManager<StyxHub>(inner)
    {
        private readonly NotificationQueue<string> _parked = new();
        private string? _stalled;
        private TaskCompletionSource _release = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public async Task Stall(Guid network, string hostName)
        {
            _release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            Volatile.Write(ref _stalled, await Registry.GetConnectionId(network, hostName));
        }

        public void Release()
        {
            Volatile.Write(ref _stalled, null);
            _release.TrySetResult();
        }

        // writes other than Receive, such as a late membership broadcast, may park too
        public async Task ParkedReceive()
        {
            while (await _parked.Next(5000, "a Receive to park on the stalled peer") != nameof(IStyxClient.Receive)) { }
        }

        public override async Task SendConnectionAsync(string connectionId, string methodName, object?[] args, CancellationToken cancellationToken = default)
        {
            if (connectionId == Volatile.Read(ref _stalled))
            {
                var release = _release.Task;
                _parked.Push(methodName);
                await release.WaitAsync(cancellationToken);
            }
            await base.SendConnectionAsync(connectionId, methodName, args, cancellationToken);
        }

        private IClientRegistry Registry => services.GetRequiredService<IClientRegistry>();
    }
}

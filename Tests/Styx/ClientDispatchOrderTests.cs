using System.Collections.Concurrent;
using System.Text;
using Cathedral.Extensions;
using Common.Interfaces;
using Hydra.Config;
using Hydra.Relay;
using Microsoft.AspNetCore.Http.Connections.Client;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.SignalR;
using Microsoft.Extensions.DependencyInjection;
using Styx;
using Styx.Services;
using Tests.Setup;

namespace Tests.Styx;

/// <summary>
/// The SignalR client runs a connection's <see cref="IStyxClient"/> handlers one at a time, in the order they arrived,
/// awaiting each before it starts the next, and a <c>Peers</c> waits its turn among the <c>Receive</c>s around it. Hydra's
/// clipboard and input code rely on it. The first handler is parked on a gate while the rest reach the client, so nothing
/// here is timed.
/// </summary>
[TestFixture]
public class ClientDispatchOrderTests : StyxFixtureBase
{
    private WrittenLifetimeManager Writes => (WrittenLifetimeManager)Factory.Services.GetRequiredService<HubLifetimeManager<StyxHub>>();

    protected override void ConfigureRelay(IServiceCollection services)
    {
        services.AddSingleton<DefaultHubLifetimeManager<StyxHub>>();
        services.AddSingleton<HubLifetimeManager<StyxHub>>(provider =>
            new WrittenLifetimeManager(provider.GetRequiredService<DefaultHubLifetimeManager<StyxHub>>()));
    }

    [Test]
    public async Task HandlersRunOneAtATime_InArrivalOrder_WithAPeersChangeAmongTheReceives()
    {
        var network = Guid.NewGuid();
        var config = await StyxTestServer.BuildNetworkConfig(Factory, network);
        // alone first, so the client sees each membership once and nothing from setup lands among what the test sends
        await using var client = await ParkingClient.Started(new ParkingClient(Factory, Profile("client", config)));
        Assert.That(await client.WaitForPeers(), Is.Empty);
        await using var sender = await HydraTestClient.Started(HydraTestClient.Master(Factory, "sender", config));
        Assert.That(await client.WaitForPeers(), Is.EqualTo(["sender"]));
        var connection = Factory.Services.GetRequiredService<IClientRegistry>().GetConnectionId(network, "client")!;

        client.ParkNextReceive();
        await SendChunk(sender, 0);
        Assert.That(await client.Entered.Next("the first chunk to park in its handler"), Is.EqualTo("chunk 0"));

        // a bulk send returns once the relay has written it to the client, so these reach it in this order
        for (var i = 1; i <= 3; i++) await SendChunk(sender, i);
        await using var joiner = await HydraTestClient.Started(HydraTestClient.Master(Factory, "joiner", config));
        await Writes.PeersWritten(connection, "joiner");
        for (var i = 4; i <= 6; i++) await SendChunk(sender, i);

        // the relay answers this behind every frame it wrote the client before, and the client reads its connection in
        // order, so once it returns the client has read them all and could start their handlers
        await client.SendReliableAsync(["sender"], TestMessages.Chunk(99)).AsTask().WaitAsync(Bound);
        Assert.That(client.Entries, Is.EqualTo(1), "a handler started while the first was still running");

        client.ReleaseParked();
        string[] expected = ["chunk 0", "chunk 1", "chunk 2", "chunk 3", "peers joiner,sender", "chunk 4", "chunk 5", "chunk 6"];
        for (var i = 1; i < expected.Length; i++) await client.Entered.Next($"handler {i + 1} of {expected.Length}");

        using (Assert.EnterMultipleScope())
        {
            Assert.That(client.Handled, Is.EqualTo(expected));
            Assert.That(client.MostRunning, Is.EqualTo(1), "two handlers ran at once");
        }
    }

    private static async Task SendChunk(HydraTestClient sender, int n) =>
        await sender.SendReliableAsync(["client"], TestMessages.Chunk(n)).AsTask().WaitAsync(TestBound.Bound);

    private static IHydraProfile Profile(string host, string networkConfig) =>
        TransitionTestHelper.Profile(host, new HydraConfig { Mode = Mode.Master, NetworkConfig = networkConfig });

    /// <summary>The relay's own lifetime manager, telling the test each write it has finished.</summary>
    private sealed class WrittenLifetimeManager(DefaultHubLifetimeManager<StyxHub> inner) : SharedHubLifetimeManager<StyxHub>(inner)
    {
        private readonly NotificationQueue<Written> _written = new();

        // a Peers listing this host, written to this connection
        public async Task PeersWritten(string connectionId, string host)
        {
            while (true)
            {
                var written = await _written.Next($"a Peers listing {host} to be written to the client");
                if (written.ConnectionId == connectionId && written.Method == nameof(IStyxClient.Peers) && ((string[])written.Args[0]!).Contains(host))
                    return;
            }
        }

        public override async Task SendConnectionAsync(string connectionId, string methodName, object?[] args, CancellationToken cancellationToken = default)
        {
            await base.SendConnectionAsync(connectionId, methodName, args, cancellationToken);
            _written.Push(new Written(connectionId, methodName, args));
        }
    }

    private sealed record Written(string ConnectionId, string Method, object?[] Args);

    /// <summary>
    /// A real relay client that records each handler as it enters it, counts how many run at once, and parks the next
    /// <c>Receive</c> on a gate when asked.
    /// </summary>
    private sealed class ParkingClient(WebApplicationFactory<global::Styx.Program> factory, IHydraProfile profile)
        : RelayConnection(profile, TestLog.CreateLogger<RelayConnection>(), new WorldState()), IAsyncDisposable
    {
        private readonly SemaphoreSlim _ready = new(0);
        private readonly NotificationQueue<string[]> _peers = new();
        private readonly ConcurrentQueue<string> _handled = new();
        private readonly RunningCounter _running = new();
        private readonly Gate _gate = new();
        private int _parkNext;
        private int _entries;

        public NotificationQueue<string> Entered { get; private set; } = new();
        public IEnumerable<string> Handled => _handled;
        public int Entries => Volatile.Read(ref _entries);
        public int MostRunning => _running.MostRunning;

        protected override TimeSpan ReconnectDelay => TimeSpan.Zero;

        public static async Task<ParkingClient> Started(ParkingClient client)
        {
            try
            {
                await client.StartAsync(CancellationToken.None);
                if (!await client._ready.WaitAsync(TestBound.Bound)) throw new TimeoutException("Timed out waiting for relay connection");
                return client;
            }
            catch
            {
                await client.DisposeAsync();
                throw;
            }
        }

        public Task<string[]> WaitForPeers() => _peers.Next("peers update");

        // forgets what ran so far, and parks the next Receive until ReleaseParked
        public void ParkNextReceive()
        {
            _handled.Clear();
            _running.Reset();
            Volatile.Write(ref _entries, 0);
            Entered = new NotificationQueue<string>();
            _gate.Hold();
            Volatile.Write(ref _parkNext, 1);
        }

        public void ReleaseParked() => _gate.Release();

        protected override void ConfigureHubUrl(HttpConnectionOptions options) => options.UseTestServer(factory.Server);

        protected override Task OnAuthenticated()
        {
            _ready.Release();
            return Task.CompletedTask;
        }

        protected override Task OnReceive(string sourceHost, MessageKind kind, ReadOnlyMemory<byte> body)
        {
            var what = kind == MessageKind.FileTransferChunk
                ? $"chunk {Encoding.UTF8.GetString(body.Span).FromSaneJson<FileTransferChunkMessage>()!.Sequence}"
                : kind.ToString();
            var parked = Interlocked.Exchange(ref _parkNext, 0) == 1 ? _gate.Passed(ConnectionToken) : Task.CompletedTask;
            return Handle(what, parked);
        }

        protected override async Task OnPeers(string[] hostNames)
        {
            await Handle($"peers {string.Join(",", hostNames)}", Task.CompletedTask);
            // after it has run, so a test that waits for it never forgets what it ran half-way through
            _peers.Push(hostNames);
        }

        private async Task Handle(string what, Task parked)
        {
            _running.Enter();
            try
            {
                Interlocked.Increment(ref _entries);
                _handled.Enqueue(what);
                Entered.Push(what);
                await parked;
                // gives a dispatcher that does not await the handler the chance to start the next one meanwhile
                await Task.Yield();
            }
            finally
            {
                _running.Exit();
            }
        }

        public async ValueTask DisposeAsync()
        {
            _gate.Release();
            using var cts = new CancellationTokenSource(TestBound.Bound);
            try { await StopAsync(cts.Token); }
            catch { /* ignore stop errors in cleanup */ }
            Dispose();
        }
    }
}

using Common.Interfaces;
using Hydra.Config;
using Hydra.Relay;
using Microsoft.AspNetCore.Http.Connections.Client;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.SignalR;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Time.Testing;
using Tests.Setup;

namespace Tests.Relay;

/// <summary>
/// A connection that never authenticated and handled nothing never connected, so its failure must not fire
/// the disconnect callbacks; one that handled traffic during the handshake must. Each test waits for the
/// NEXT attempt's auth timer, which proves the failed attempt has finished unwinding, callbacks included.
/// </summary>
[TestFixture]
public class RelayAuthFailureTests
{
    private static WebApplicationFactory<global::Styx.Program>? _open;
    private static WebApplicationFactory<global::Styx.Program>? _stalled;
    private static WebApplicationFactory<global::Styx.Program>? _stalledAfterPeers;

    [OneTimeSetUp]
    public static void OneTimeSetUp()
    {
        _open = StyxTestServer.Create();
        _ = _open.Server;
        _stalled = StyxTestServer.Create(configure: services =>
            services.Configure<HubOptions>(options => options.AddFilter(new StallAuthentication())));
        _ = _stalled.Server;
        _stalledAfterPeers = StyxTestServer.Create(configure: services =>
            services.Configure<HubOptions>(options => options.AddFilter(new StallAuthentication(sendPeersFirst: true))));
        _ = _stalledAfterPeers.Server;
    }

    [OneTimeTearDown]
    public static async Task OneTimeTearDown()
    {
        if (_open != null) await _open.DisposeAsync();
        if (_stalled != null) await _stalled.DisposeAsync();
        if (_stalledAfterPeers != null) await _stalledAfterPeers.DisposeAsync();
    }

    [Test]
    public async Task RejectedLogin_FiresNoDisconnectCallbacks()
    {
        var cfg = await StyxTestServer.BuildNetworkConfig(_open!, Guid.NewGuid(), password: "not-the-relay-password");
        await using var probe = new AuthProbe(_open!, cfg);
        await probe.StartAsync(CancellationToken.None);

        await probe.Clock.NextTimer();
        await probe.Clock.NextTimer();

        Assert.That(probe.Disconnects, Is.Zero);
    }

    [Test]
    public async Task TimedOutLogin_FiresNoDisconnectCallbacks()
    {
        await using var probe = new AuthProbe(_stalled!, await StyxTestServer.BuildNetworkConfig(_stalled!, Guid.NewGuid()));
        await probe.StartAsync(CancellationToken.None);

        await probe.Clock.NextTimer();
        probe.Clock.TimeOut();
        await probe.Clock.NextTimer();

        Assert.That(probe.Disconnects, Is.Zero);
    }

    [Test]
    public async Task CancelledLogin_FiresNoDisconnectCallbacks()
    {
        await using var probe = new AuthProbe(_stalled!, await StyxTestServer.BuildNetworkConfig(_stalled!, Guid.NewGuid()));
        await probe.StartAsync(CancellationToken.None);

        await probe.Clock.NextTimer();
        Assert.That(probe.RequestReconnect(), Is.True);
        await probe.Clock.NextTimer();

        Assert.That(probe.Disconnects, Is.Zero);
    }

    [Test]
    public async Task TimedOutLogin_AfterHandlingTraffic_FiresDisconnectCallbacks()
    {
        await using var probe = new AuthProbe(_stalledAfterPeers!, await StyxTestServer.BuildNetworkConfig(_stalledAfterPeers!, Guid.NewGuid()));
        await probe.StartAsync(CancellationToken.None);

        await probe.Clock.NextTimer();
        await probe.PeersHandled.WaitAsync(TimeSpan.FromSeconds(15));
        probe.Clock.TimeOut();
        await probe.Clock.NextTimer();

        Assert.That(probe.Disconnects, Is.EqualTo(2), "OnDisconnected and Disconnected each fire once");
    }

    // parks every Authenticate until its connection goes away, optionally after handing the caller a peer list
    private sealed class StallAuthentication(bool sendPeersFirst = false) : IHubFilter
    {
        private static readonly string[] EarlyPeers = ["early-peer"];

        public async ValueTask<object?> InvokeMethodAsync(HubInvocationContext context, Func<HubInvocationContext, ValueTask<object?>> next)
        {
            if (context.HubMethodName == nameof(IStyxServer.Authenticate))
            {
                if (sendPeersFirst)
                    await context.Hub.Clients.Caller.SendAsync(nameof(IStyxClient.Peers), EarlyPeers, context.Context.ConnectionAborted);
                await Task.Delay(Timeout.Infinite, context.Context.ConnectionAborted);
            }
            return await next(context);
        }
    }

    // hands each auth timer to the test as it is armed; nothing fires until the test steps past the auth timeout
    private sealed class SteppedAuthClock : FakeTimeProvider
    {
        private readonly NotificationQueue<ITimer> _timers = new();

        public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
        {
            var timer = base.CreateTimer(callback, state, dueTime, period);
            _timers.Push(timer);
            return timer;
        }

        internal Task<ITimer> NextTimer() => _timers.Next(15000, "an auth timer");
        internal void TimeOut() => Advance(TimeSpan.FromSeconds(Constants.AuthTimeoutSeconds));
    }

    private sealed class AuthProbe : RelayConnection, IAsyncDisposable
    {
        private readonly WebApplicationFactory<global::Styx.Program> _factory;
        private int _disconnects;
        private readonly TaskCompletionSource _peersHandled = new(TaskCreationOptions.RunContinuationsAsynchronously);

        internal AuthProbe(WebApplicationFactory<global::Styx.Program> factory, string networkConfig)
            : base(TransitionTestHelper.Profile("auth-probe", new HydraConfig { Mode = Mode.Master, NetworkConfig = networkConfig }),
                TestLog.CreateLogger<RelayConnection>(), new WorldState())
        {
            _factory = factory;
            Disconnected += () =>
            {
                Interlocked.Increment(ref _disconnects);
                return Task.CompletedTask;
            };
        }

        internal SteppedAuthClock Clock { get; } = new();
        internal int Disconnects => Volatile.Read(ref _disconnects);
        internal Task PeersHandled => _peersHandled.Task;

        protected override TimeProvider AuthClock => Clock;
        protected override TimeSpan ReconnectDelay => TimeSpan.Zero;

        protected override void ConfigureHubUrl(HttpConnectionOptions options) =>
            options.UseTestServer(_factory.Server);

        protected override async Task OnPeers(string[] hostNames)
        {
            await base.OnPeers(hostNames);
            _peersHandled.TrySetResult();
        }

        protected override Task OnDisconnected()
        {
            Interlocked.Increment(ref _disconnects);
            return Task.CompletedTask;
        }

        public async ValueTask DisposeAsync()
        {
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            await StopAsync(cts.Token);
            Dispose();
        }
    }
}

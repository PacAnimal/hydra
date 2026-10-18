using System.Collections.Concurrent;
using System.Security.Claims;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.Extensions.Logging.Abstractions;
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
/// A peer whose download has stalled while its pings still arrive never trips the client timeout, so the relay drops one
/// that takes no message for <see cref="PeerWriter.Deadline"/> while messages wait for it. Writes to the slow peer park in
/// its lifetime manager as they would on a full transport, until the test lets the peer take them, and the deadline runs
/// on a fake clock whose timers fire a little early, as a real one can by a precise clock, so nothing here is timed.
/// </summary>
[TestFixture]
public class StalledPeerTests : StyxFixtureBase
{
    private readonly FakeTimeProvider _clock = new();
    private NotificationQueue<string> _queuedReceives = new();
    private string? _slowPeer;

    private StallingLifetimeManager Writes => (StallingLifetimeManager)Factory.Services.GetRequiredService<HubLifetimeManager<StyxHub>>();

    protected override void ConfigureRelay(IServiceCollection services)
    {
        services.AddKeyedSingleton<TimeProvider>(typeof(PeerWriter), new EarlyTimers(_clock));
        services.AddSingleton<DefaultHubLifetimeManager<StyxHub>>();
        services.AddSingleton<HubLifetimeManager<StyxHub>>(provider => new StallingLifetimeManager(provider.GetRequiredService<DefaultHubLifetimeManager<StyxHub>>()));
    }

    [OneTimeSetUp]
    public void WatchTheQueue() =>
        Factory.Services.GetRequiredService<PeerWriter>().Queued += (connectionId, method) =>
        {
            if (method == nameof(IStyxClient.Receive)) _queuedReceives.Push(connectionId);
        };

    [SetUp]
    public void ForgetTheLastTest() => _queuedReceives = new NotificationQueue<string>();

    [TearDown]
    public void ReleaseEverything()
    {
        Volatile.Write(ref _slowPeer, null);
        Writes.Release();
    }

    /// <summary>Both senders' invocations end with the dropped peer, the one in flight and the one queued behind it.</summary>
    [Test]
    public async Task APeerThatTakesNothingWithinTheDeadline_IsDropped_AndEverySenderMovesOn()
    {
        await using var peers = await ConnectedPeers("sender", "other", "receiver", "slow");
        await SlowDown(peers, "slow");

        var inFlight = peers["sender"].Server!.Send(["slow"], [1]);
        await QueuedFor(_slowPeer!, "the sender's message to be queued");
        await Writes.ParkedReceive();
        var queued = peers["other"].Server!.Send(["slow"], [2]);
        await QueuedFor(_slowPeer!, "the other sender's message to be queued");
        PassTheDeadline();

        await Task.WhenAll(inFlight, queued).WaitAsync(Bound);
        await peers["slow"].WaitForClose();
        await peers["sender"].Server!.Send(["receiver"], [3]).WaitAsync(Bound);
        Assert.That(await peers["receiver"].NextReceived(), Is.EqualTo(new byte[] { 3 }));
    }

    /// <summary>
    /// A Send queues its delivery to every target at once, so a slow target in one sender's list neither holds up its other
    /// targets nor lets a later sender's message overtake it at them.
    /// </summary>
    [Test]
    public async Task ASlowTargetInOneSend_DoesNotHoldUpItsOtherTargets()
    {
        await using var peers = await ConnectedPeers("first", "second", "receiver", "slow");
        await SlowDown(peers, "slow");

        var parked = peers["first"].Server!.Send(["slow", "receiver"], [1]);
        await Writes.ParkedReceive();
        Assert.That(await peers["receiver"].NextReceived(), Is.EqualTo(new byte[] { 1 }));
        await peers["second"].Server!.Send(["receiver"], [2]).WaitAsync(Bound);
        Assert.That(await peers["receiver"].NextReceived(), Is.EqualTo(new byte[] { 2 }));

        Writes.Release();
        await parked.WaitAsync(Bound);
        Assert.That(await peers["slow"].NextReceived(), Is.EqualTo(new byte[] { 1 }));
    }

    /// <summary>The clock runs only while something waits: a peer idle for an hour still has the whole deadline.</summary>
    [Test]
    public async Task APeerThatTakesTheMessageWithinTheDeadline_StaysConnected()
    {
        await using var peers = await ConnectedPeers("sender", "slow");
        await SlowDown(peers, "slow");
        _clock.Advance(TimeSpan.FromHours(1));

        var parked = peers["sender"].Server!.Send(["slow"], [1]);
        var write = await Writes.ParkedReceive();
        _clock.Advance(PeerWriter.Deadline - TimeSpan.FromTicks(1));
        write.Take();

        await parked.WaitAsync(Bound);
        Assert.That(await peers["slow"].NextReceived(), Is.EqualTo(new byte[] { 1 }));
        Writes.Release();
        await peers["sender"].Server!.Send(["slow"], [2]).WaitAsync(Bound);
        using (Assert.EnterMultipleScope())
        {
            Assert.That(await peers["slow"].NextReceived(), Is.EqualTo(new byte[] { 2 }));
            Assert.That(peers["slow"].State, Is.EqualTo(HubConnectionState.Connected));
        }
    }

    /// <summary>
    /// Three senders queue a message each for one slow peer, which takes each well inside the deadline but all of them in
    /// longer than it. None is dropped, since every message taken restarts the clock, and they arrive in the order the
    /// relay queued them.
    /// </summary>
    [Test]
    public async Task WritersQueuedForAPeerThatKeepsTakingThem_AreNotDropped_AndArriveInTheOrderTheyCame()
    {
        string[] senders = ["first", "second", "third"];
        await using var peers = await ConnectedPeers([.. senders, "slow"]);
        await SlowDown(peers, "slow");

        var sends = new List<Task>();
        for (var i = 0; i < senders.Length; i++)
        {
            sends.Add(peers[senders[i]].Server!.Send(["slow"], [(byte)i]));
            await QueuedFor(_slowPeer!, $"the {senders[i]} sender's message to be queued");
        }

        for (var i = 0; i < senders.Length; i++)
        {
            var write = await Writes.ParkedReceive();
            _clock.Advance(PeerWriter.Deadline * 0.6);
            write.Take();
        }
        await Task.WhenAll(sends).WaitAsync(Bound);

        var arrived = new List<byte>();
        for (var i = 0; i < senders.Length; i++) arrived.Add((await peers["slow"].NextReceived())[0]);
        using (Assert.EnterMultipleScope())
        {
            Assert.That(arrived, Is.EqualTo(new byte[] { 0, 1, 2 }));
            Assert.That(peers["slow"].State, Is.EqualTo(HubConnectionState.Connected));
        }
    }

    /// <summary>
    /// A sender held inside its first target's write keeps its place at its second: every place is reserved before any write
    /// starts, so a later sender's message waits behind it there rather than overtaking it.
    /// </summary>
    [Test]
    public async Task ASenderHeldInsideItsFirstTargetsWrite_KeepsItsPlaceAtTheNext()
    {
        await using var peers = await ConnectedPeers("first", "second", "receiver", "slow");
        await SlowDown(peers, "slow", synchronously: true);
        var receiver = await ConnectionOf(peers, "receiver");

        var first = peers["first"].Server!.Send(["slow", "receiver"], [1]);
        var write = await Writes.ParkedReceive();
        var second = peers["second"].Server!.Send(["receiver"], [2]);
        await QueuedFor(receiver, "the first sender's place at the receiver");
        await QueuedFor(receiver, "the second sender's message to queue behind it");
        write.Take();

        await Task.WhenAll(first, second).WaitAsync(Bound);
        Assert.That(new[] { (await peers["receiver"].NextReceived())[0], (await peers["receiver"].NextReceived())[0] }, Is.EqualTo(new byte[] { 1, 2 }));
    }

    /// <summary>
    /// A sender that wrote its own message is done once it is taken: the messages queued behind it are drained on another
    /// thread, not on its own, which would hold its Send, and everything it sends after, for other senders' traffic.
    /// </summary>
    [Test]
    public async Task ASenderIsNotHeldForTheMessagesQueuedBehindIt()
    {
        await using var peers = await ConnectedPeers("first", "second", "slow");
        await SlowDown(peers, "slow", synchronously: true);

        var first = peers["first"].Server!.Send(["slow"], [1]);
        var write = await Writes.ParkedReceive();
        var second = peers["second"].Server!.Send(["slow"], [2]);
        await QueuedFor(_slowPeer!, "the first sender's message to be queued");
        await QueuedFor(_slowPeer!, "the second sender's message to queue behind it");
        write.Take();

        await first.WaitAsync(Bound);
        (await Writes.ParkedReceive()).Take();
        await second.WaitAsync(Bound);
    }

    /// <summary>
    /// Membership snapshots waiting next to each other collapse into the latest, which bounds them by the deliveries between
    /// them. The one showing a displaced host leaving never collapses, since a peer must see the host leave before it
    /// returns; the arrival after it is a live snapshot like any other.
    /// </summary>
    [Test]
    public async Task SnapshotsWaitingTogether_CollapseIntoTheLatest_ButNeverTheOneShowingADisplacedHostLeave()
    {
        var hub = new RecordingLifetimeManager();
        var writer = new PeerWriter(hub, new FakeTimeProvider(), NullLogger<PeerWriter>.Instance);
        writer.Track(new FakeCaller("peer"));

        var inFlight = writer.Peers("peer", ["a"], collapsible: true);
        var live = new[] { writer.Peers("peer", ["a", "b"], collapsible: true), writer.Peers("peer", ["a", "b", "c"], collapsible: true) };
        var leaving = writer.Peers("peer", ["a"], collapsible: false);
        var arrivals = new[] { writer.Peers("peer", ["a", "c"], collapsible: true), writer.Peers("peer", ["c"], collapsible: true) };
        hub.Release();

        await Task.WhenAll([inFlight, .. live, leaving, .. arrivals]).WaitAsync(Bound);
        Assert.That(hub.Written, Is.EqualTo(["a", "a,b,c", "a", "c"]));
    }

    /// <summary>
    /// A broadcast still waiting on a slow peer does not hold up the next: a displacement queued behind it is in every other
    /// peer's order before the displacing client is answered, so its first payload cannot overtake the host leaving.
    /// </summary>
    [Test]
    public async Task ADisplacementBehindASlowPeersBroadcast_StillReachesTheOthersAheadOfTheNewcomer()
    {
        await using var peers = await ConnectedPeers("slow", "twin", "watcher");
        await SlowDown(peers, "slow");

        await using var joiner = new TestStyxClient();
        var token = StyxTestServer.MintToken(peers.Network).ToString();
        Assert.That((await joiner.Connect(Factory, token, "joiner")).Authenticated, Is.True);
        await peers["watcher"].WaitForPeers(["slow", "twin", "joiner"], anyOrder: true);

        await using var newcomer = new TestStyxClient();
        Assert.That((await newcomer.Connect(Factory, token, "twin")).Authenticated, Is.True);
        await newcomer.Server!.Send(["watcher"], [7]).WaitAsync(Bound);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(await peers["watcher"].NextReceived(), Is.EqualTo(new byte[] { 7 }));
            Assert.That(peers["watcher"].PeersWaiting, Is.Positive, "the newcomer's payload overtook the host leaving");
            Assert.That(await peers["watcher"].WaitForPeers(), Does.Not.Contain("twin"), "the first membership after the payload must show the host leaving");
        }
    }

    // in two steps, as real time passes, so the early timer fires before the deadline and must wait out the rest
    private void PassTheDeadline()
    {
        _clock.Advance(PeerWriter.Deadline - EarlyTimers.Early);
        _clock.Advance(EarlyTimers.Early);
    }

    // a synchronous stall holds the writing thread, as a write that completes inline would until it does
    private async Task SlowDown(PeerClients peers, string hostName, bool synchronously = false)
    {
        var connectionId = await ConnectionOf(peers, hostName);
        Volatile.Write(ref _slowPeer, connectionId);
        Writes.Stall(connectionId, synchronously);
    }

    private async Task<string> ConnectionOf(PeerClients peers, string hostName) =>
        (await Factory.Services.GetRequiredService<IClientRegistry>().GetConnectionId(peers.Network, hostName))!;

    // a Receive has joined this connection's queue
    private async Task QueuedFor(string connectionId, string what)
    {
        while (await _queuedReceives.Next(what) != connectionId) { }
    }

    /// <summary>
    /// The relay's own lifetime manager, parking every write to one connection until the test lets the peer take it, the
    /// write is cancelled, or the connection ends, which is when a real transport's stuck flush returns.
    /// </summary>
    private sealed class StallingLifetimeManager(DefaultHubLifetimeManager<StyxHub> inner) : SharedHubLifetimeManager<StyxHub>(inner)
    {
        private readonly NotificationQueue<Parked> _parked = new();
        private readonly Lock _lock = new();
        private readonly List<Parked> _waiting = [];
        private string? _stalled;
        private bool _synchronously;

        public void Stall(string connectionId, bool synchronously)
        {
            lock (_lock)
            {
                _stalled = connectionId;
                _synchronously = synchronously;
            }
        }

        // stops stalling, and lets every parked write through
        public void Release()
        {
            List<Parked> waiting;
            lock (_lock)
            {
                _stalled = null;
                waiting = [.. _waiting];
                _waiting.Clear();
            }
            foreach (var parked in waiting) parked.Take();
        }

        // a write parked ahead of it that is not a Receive, such as a late membership broadcast, is let through
        public async Task<Parked> ParkedReceive()
        {
            while (true)
            {
                var parked = await _parked.Next("a Receive to park on the slow peer");
                if (parked.Method == nameof(IStyxClient.Receive)) return parked;
                parked.Take();
            }
        }

        public override async Task SendConnectionAsync(string connectionId, string methodName, object?[] args, CancellationToken cancellationToken = default)
        {
            Parked? parked = null;
            var synchronously = false;
            lock (_lock)
            {
                if (connectionId == _stalled)
                {
                    synchronously = _synchronously;
                    parked = new Parked(methodName);
                    _waiting.Add(parked);
                }
            }
            if (parked != null)
            {
                _parked.Push(parked);
                if (synchronously) parked.Taken.Passed(cancellationToken).GetAwaiter().GetResult();
                else await parked.Taken.Passed(cancellationToken);
            }
            await base.SendConnectionAsync(connectionId, methodName, args, cancellationToken);
        }

        public override async Task OnDisconnectedAsync(HubConnectionContext connection)
        {
            bool stalled;
            lock (_lock) stalled = connection.ConnectionId == _stalled;
            if (stalled) Release();
            await base.OnDisconnectedAsync(connection);
        }
    }

    /// <summary>The fake clock, its timers firing a millisecond before they are due.</summary>
    private sealed class EarlyTimers(FakeTimeProvider inner) : TimeProvider
    {
        public static readonly TimeSpan Early = TimeSpan.FromMilliseconds(1);

        public override long TimestampFrequency => inner.TimestampFrequency;

        public override long GetTimestamp() => inner.GetTimestamp();

        public override DateTimeOffset GetUtcNow() => inner.GetUtcNow();

        public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period) =>
            new EarlyTimer(inner.CreateTimer(callback, state, Earlier(dueTime), period));

        private static TimeSpan Earlier(TimeSpan due) => due > Early * 2 ? due - Early : due;

        private sealed class EarlyTimer(ITimer timer) : ITimer
        {
            public bool Change(TimeSpan dueTime, TimeSpan period) => timer.Change(Earlier(dueTime), period);

            public void Dispose() => timer.Dispose();

            public ValueTask DisposeAsync() => timer.DisposeAsync();
        }
    }

    /// <summary>Records each Peers written, holding every write until released.</summary>
    private sealed class RecordingLifetimeManager()
        : SharedHubLifetimeManager<StyxHub>(new DefaultHubLifetimeManager<StyxHub>(NullLogger<DefaultHubLifetimeManager<StyxHub>>.Instance))
    {
        private readonly Gate _writes = new(held: true);
        private readonly ConcurrentQueue<string> _written = new();

        public IEnumerable<string> Written => _written;

        public void Release() => _writes.Release();

        public override async Task SendConnectionAsync(string connectionId, string methodName, object?[] args, CancellationToken cancellationToken = default)
        {
            await _writes.Passed(cancellationToken);
            _written.Enqueue(string.Join(",", (string[])args[0]!));
        }
    }

    private sealed class FakeCaller(string connectionId) : HubCallerContext
    {
        public override string ConnectionId => connectionId;
        public override string? UserIdentifier => null;
        public override ClaimsPrincipal? User => null;
        public override IDictionary<object, object?> Items { get; } = new Dictionary<object, object?>();
        public override IFeatureCollection Features { get; } = new FeatureCollection();
        public override CancellationToken ConnectionAborted => CancellationToken.None;

        public override void Abort() { }
    }

    private sealed record Parked(string Method)
    {
        public Gate Taken { get; } = new(held: true);

        public void Take() => Taken.Release();
    }
}

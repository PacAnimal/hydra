using Cathedral.Styx;
using Common.DTO;
using Common.Interfaces;
using Hydra.Relay;
using Microsoft.AspNetCore.SignalR;
using Microsoft.Extensions.DependencyInjection;
using Styx.Services;
using Tests.Setup;

namespace Tests.Styx;

/// <summary>
/// How many of one client's invocations the relay runs at once, seen from inside the hub: one on <c>/relay</c>, which is
/// what keeps a client that does not wait for each Send in order, and the configured width on <c>/relay/parallel</c>.
/// Nothing here is timed: an invocation is parked on a gate and the test waits until it is.
/// </summary>
[TestFixture]
public class RelayDispatchTests : StyxFixtureBase
{
    private readonly InvocationProbe _sends = new(nameof(IStyxServer.Send));
    private readonly InvocationProbe _logins = new(nameof(IStyxServer.Authenticate));
    // resolved, not remembered from the factory, so it exists before the relay's first login
    private HeldVerifier Verifier => (HeldVerifier)Factory.Services.GetRequiredService<ITokenVerifier>();

    protected override void ConfigureRelay(IServiceCollection services)
    {
        services.Configure<HubOptions>(hub =>
        {
            hub.AddFilter(_sends);
            hub.AddFilter(_logins);
        });
        services.AddSingleton<ITokenVerifier>(provider => new HeldVerifier(ActivatorUtilities.CreateInstance<TokenVerifier>(provider)));
    }

    [SetUp]
    public void ForgetTheLastTest()
    {
        _sends.Reset();
        _logins.Reset();
        Verifier.Release();
    }

    [TearDown]
    public void ReleaseEverything()
    {
        _sends.Release();
        Verifier.Release();
    }

    /// <summary>
    /// Sends that do not wait for each other arrive in the order they were sent, because the relay starts a client's next
    /// invocation only once the last has finished. The first is parked inside the hub while the rest are already written.
    /// </summary>
    [Test]
    public async Task OnTheOrderedPath_SendsThatDoNotWaitArriveInOrder()
    {
        await using var pair = await ConnectedRawPair(global::Styx.Constants.RelayPath);
        var (sender, receiver) = pair;

        _sends.Hold();
        const int count = 20;
        // each completes once written to the transport, never waiting on the relay, so all of them are written before
        // the relay has finished the first
        for (var i = 0; i < count; i++) await sender.SendWithoutWaiting(["receiver"], [(byte)i]).WaitAsync(Bound);
        await _sends.Held.Next(5000, "the first send to be parked in the hub");
        _sends.Release();

        var arrived = new List<int>();
        for (var i = 0; i < count; i++) arrived.Add((await receiver.NextReceived())[0]);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(arrived, Is.EqualTo(Enumerable.Range(0, count)));
            Assert.That(_sends.MostRunning, Is.EqualTo(1), "the relay ran two of one client's sends at once, so they could overtake each other");
        }
    }

    /// <summary>The parallel path runs as many of a client's sends at once as its width, and never one more.</summary>
    [Test]
    public async Task OnTheParallelPath_AsManySendsRunAtOnceAsItsWidth()
    {
        await using var pair = await ConnectedRawPair(global::Styx.Constants.ParallelRelayPath);
        var (sender, receiver) = pair;

        const int width = global::Styx.Constants.DefaultParallelInvocations;
        _sends.Hold();
        for (var i = 0; i <= width; i++) await sender.SendWithoutWaiting(["receiver"], [(byte)i]).WaitAsync(Bound);
        for (var i = 0; i < width; i++) await _sends.Held.Next(5000, $"send {i + 1} of {width} to be parked in the hub at once");
        _sends.Release();

        var arrived = new List<int>();
        for (var i = 0; i <= width; i++) arrived.Add((await receiver.NextReceived())[0]);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(arrived, Is.EquivalentTo(Enumerable.Range(0, width + 1)));
            Assert.That(_sends.MostRunning, Is.EqualTo(width));
        }
    }

    /// <summary>
    /// Hydra's input lane sends its next frame while the relay is still running the last one: it does not wait for the
    /// hub method to return. A lane that waited would never reach the next frame's encrypt step while the first is parked.
    /// </summary>
    [Test]
    public async Task TheInputLaneSendsItsNextFrameWithoutWaitingForTheRelay()
    {
        await using var pair = await ConnectedPair(peerTakesBundles: false);
        var (sender, receiver) = pair;

        _sends.Hold(async invocation => await HostOf(invocation) == "sender");
        sender.Send(["receiver"], TestMessages.Key(1));
        await _sends.Held.Next(5000, "the sender's frame to be parked in the hub");

        sender.HoldLane(RelayLane.Input);
        sender.Send(["receiver"], TestMessages.Key(2));
        await sender.WaitUntilLaneHeld();

        sender.ReleaseLane();
        _sends.Release();

        var keys = new List<string?>();
        while (keys.Count < 2)
        {
            var (_, kind, json) = await receiver.WaitForNextMessage();
            if (kind == MessageKind.KeyEvent) keys.Add(CharacterOf(json));
        }
        Assert.That(keys, Is.EqualTo(["b", "c"]));
    }

    /// <summary>
    /// A connection logs in once at a time on every path, so the login throttle caps it at one a second however many
    /// invocations the path runs at once. The first login is parked in the token check; the second has started.
    /// </summary>
    [Test]
    public async Task OnTheParallelPath_AConnectionLogsInOnceAtATime()
    {
        await using var client = new TestStyxClient();
        await client.ConnectRaw(Factory, global::Styx.Constants.ParallelRelayPath);
        var network = StyxToken.NewNetworkId();

        Verifier.Hold();
        var first = client.Server!.Authenticate(Login(network, "first"));
        await Verifier.Entered.Next(5000, "the first login to reach the token check");
        var second = client.Server!.Authenticate(Login(network, "second"));
        await _logins.Started.Next(5000, "the first login to start");
        await _logins.Started.Next(5000, "the second login to start");

        Assert.That(Verifier.MostRunning, Is.EqualTo(1), "a second login on the same connection ran while the first was still being checked");

        Verifier.Release();
        var answers = await Task.WhenAll(first, second).WaitAsync(Bound);
        using (Assert.EnterMultipleScope())
        {
            Assert.That(answers.Select(a => a.Authenticated), Is.All.True);
            Assert.That(Verifier.MostRunning, Is.EqualTo(1));
        }
    }

    /// <summary>A login waiting its turn on a connection that drops is ended with it, as is the one ahead of it.</summary>
    [Test]
    public async Task ALoginWaitingItsTurn_EndsWhenItsConnectionDrops()
    {
        var client = new TestStyxClient();
        await client.ConnectRaw(Factory, global::Styx.Constants.ParallelRelayPath);
        var network = StyxToken.NewNetworkId();

        Verifier.Hold();
        var first = client.Server!.Authenticate(Login(network, "first"));
        await Verifier.Entered.Next(5000, "the first login to reach the token check");
        var second = client.Server!.Authenticate(Login(network, "second"));
        await _logins.Started.Next(5000, "the first login to start");
        await _logins.Started.Next(5000, "the second login to start, and wait its turn");

        await client.DisposeAsync();
        await _logins.Finished.Next(5000, "one login to end with its connection");
        await _logins.Finished.Next(5000, "the other login to end with its connection");

        // the client gave up on both when it stopped; WhenAny observes that without rethrowing it
        await Task.WhenAny(Task.WhenAll(first, second)).WaitAsync(Bound);
        using (Assert.EnterMultipleScope())
        {
            Assert.That(Verifier.MostRunning, Is.EqualTo(1));
            Assert.That(first.IsCompletedSuccessfully || second.IsCompletedSuccessfully, Is.False);
        }
    }

    // the receiver always on /relay; disposed together even when a connect fails
    private async Task<RawPair> ConnectedRawPair(string senderPath)
    {
        var token = StyxTestServer.MintToken().ToString();
        var pair = new RawPair(new TestStyxClient(), new TestStyxClient());
        try
        {
            await pair.Sender.Connect(Factory, token, "sender", senderPath);
            await pair.Receiver.Connect(Factory, token, "receiver");
            return pair;
        }
        catch
        {
            await pair.DisposeAsync();
            throw;
        }
    }

    private static RelayLogin Login(Guid network, string hostName) =>
        new() { Authorization = StyxTestServer.MintToken(network).ToString(), HostName = hostName };

    private static async ValueTask<string?> HostOf(HubInvocationContext invocation) =>
        (await invocation.ServiceProvider.GetRequiredService<IClientRegistry>().GetIdentity(invocation.Context.ConnectionId))?.HostName;

    private static string? CharacterOf(string json) => System.Text.Json.JsonDocument.Parse(json).RootElement.GetProperty("character").GetString();

    private sealed record RawPair(TestStyxClient Sender, TestStyxClient Receiver) : IAsyncDisposable
    {
        public async ValueTask DisposeAsync()
        {
            try
            {
                await Sender.DisposeAsync();
            }
            finally
            {
                await Receiver.DisposeAsync();
            }
        }
    }

    /// <summary>The relay's own verifier behind a gate, counting how many checks run at once.</summary>
    private sealed class HeldVerifier(ITokenVerifier inner) : ITokenVerifier
    {
        private TaskCompletionSource? _hold;
        private int _running;
        private int _mostRunning;

        public NotificationQueue<string> Entered { get; private set; } = new();
        public int MostRunning => Volatile.Read(ref _mostRunning);

        public void Hold()
        {
            Volatile.Write(ref _mostRunning, 0);
            Entered = new NotificationQueue<string>();
            _hold = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        }

        public void Release() => Interlocked.Exchange(ref _hold, null)?.TrySetResult();

        public async Task<TokenVerdict> Verify(string authorization, string remoteIp, CancellationToken cancel)
        {
            var running = Interlocked.Increment(ref _running);
            for (var most = MostRunning; running > most; most = MostRunning)
                Interlocked.CompareExchange(ref _mostRunning, running, most);
            try
            {
                Entered.Push(authorization);
                if (Volatile.Read(ref _hold) is { } hold) await hold.Task.WaitAsync(cancel);
                return await inner.Verify(authorization, remoteIp, cancel);
            }
            finally
            {
                Interlocked.Decrement(ref _running);
            }
        }
    }
}

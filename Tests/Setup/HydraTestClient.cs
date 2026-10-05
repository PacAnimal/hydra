using System.Text;
using Cathedral.Utils;
using Hydra.Config;
using Hydra.Relay;
using Microsoft.AspNetCore.Http.Connections.Client;
using Microsoft.AspNetCore.Mvc.Testing;

namespace Tests.Setup;

/// <summary>
/// Test wrapper around the real RelayConnection — uses the actual Hydra relay code including RelayEncryption.
/// Use this for end-to-end tests that prove the full Hydra relay stack works as intended.
/// Use TestStyxClient instead when you need to test protocol-level edge cases.
/// </summary>
public sealed class HydraTestClient : RelayConnection, IAsyncDisposable
{
    private readonly WebApplicationFactory<global::Styx.Program> _factory;

    /// <summary>
    /// The state this client sends against, so a test can say what its peers are capable of. A master learns
    /// that from a peer's ScreenInfo in production; a test that only wants to drive the send path says it
    /// directly rather than staging a whole handshake to reach one boolean.
    /// </summary>
    public IWorldState World { get; }

    /// <summary>
    /// Not a primary constructor, and not `world ?? new WorldState()` written twice: that reads as one
    /// default and is two, so the base would send against one instance while <see cref="World"/> handed the
    /// test another — and every capability a test set would be invisible to the code under test.
    /// </summary>
    public HydraTestClient(WebApplicationFactory<global::Styx.Program> factory, IHydraProfile profile, IWorldState? world = null)
        : this(factory, profile, Shared(world)) { }

    private HydraTestClient(WebApplicationFactory<global::Styx.Program> factory, IHydraProfile profile, (IWorldState World, bool _) shared)
        : base(profile, TestLog.CreateLogger<RelayConnection>(), shared.World)
    {
        _factory = factory;
        World = shared.World;
    }

    /// <summary>Evaluates the default exactly once, so the base and <see cref="World"/> share one instance.</summary>
    private static (IWorldState, bool) Shared(IWorldState? world) => (world ?? new WorldState(), true);

    // a master named host on the network the config describes
    public static HydraTestClient Master(WebApplicationFactory<global::Styx.Program> factory, string host, string networkConfig) =>
        new(factory, TransitionTestHelper.Profile(host, new HydraConfig { Mode = Mode.Master, NetworkConfig = networkConfig }));

    private readonly Toggle _disposed = new();
    public bool Disposed => _disposed;

    private readonly SemaphoreSlim _readySignal = new(0);
    private readonly NotificationQueue<string[]> _peers = new();
    private readonly NotificationQueue<(string Source, MessageKind Kind, string Json)> _messages = new();
    private readonly SemaphoreSlim _receiveSignal = new(0);
    private readonly SemaphoreSlim _kickSignal = new(0);
    private readonly SemaphoreSlim _parkedSignal = new(0);

    private (string Source, MessageKind Kind, string Json)? _lastMessage;
    private string? _kickReason;

    private volatile LaneGate? _gate;

    public (string Source, MessageKind Kind, string Json)? LastMessage => _lastMessage;
    public string? KickReason => _kickReason;

    protected override TimeSpan ReconnectDelay => TimeSpan.Zero;

    protected override Task OnAuthenticated() { _readySignal.Release(); return Task.CompletedTask; }

    protected override void OnParkedUntilResumed() => _parkedSignal.Release();

    /// <summary>
    /// Parks one lane just before it encrypts, until <see cref="ReleaseLane"/>.
    ///
    /// <para>A GATE, never a delay: the claims under test are about what can overtake what, and a test that
    /// raced a big payload against a small one would be asserting that this machine happened to be fast
    /// enough. <see cref="WaitUntilLaneHeld"/> says when the lane is genuinely parked, so a test waits on a fact.</para>
    ///
    /// <para>Either lane, because both need parking: the bulk one to show input overtaking it, the input
    /// one to show a dropped connection failing what is queued there. Holding one says nothing about the
    /// other — that is the whole point of them being separate.</para>
    /// </summary>
    public void HoldLane(RelayLane lane) => _gate = new LaneGate(lane);

    // blocks until a payload is parked on the lane HoldLane gated
    public async Task WaitUntilLaneHeld(int timeoutMs = 15000)
    {
        var gate = _gate ?? throw new InvalidOperationException("no lane is held");
        try { await gate.Held.Task.WaitAsync(TimeSpan.FromMilliseconds(timeoutMs)); }
        catch (TimeoutException) { throw new TimeoutException($"Timed out waiting for the {gate.Lane} lane to park"); }
    }

    /// <summary>Lets the parked lane through, and STOPS GATING, so later payloads pass untouched.</summary>
    public void ReleaseLane() => Interlocked.Exchange(ref _gate, null)?.Release.TrySetResult();

    protected override async ValueTask<byte[]> EncryptForSend(byte[] payload, CancellationToken cancel)
    {
        if (_gate is { } gate && MessageLane.Of(payload) == gate.Lane)
        {
            gate.Held.TrySetResult();
            await gate.Release.Task.WaitAsync(cancel);
        }

        return await base.EncryptForSend(payload, cancel);
    }

    private sealed class LaneGate(RelayLane lane)
    {
        public RelayLane Lane { get; } = lane;
        public TaskCompletionSource Held { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    }

    // route the hub connection through the in-memory test server handler
    protected override void ConfigureHubUrl(HttpConnectionOptions options)
    {
        options.UseTestServer(_factory.Server);
    }

    protected override Task OnReceive(string sourceHost, MessageKind kind, ReadOnlyMemory<byte> body)
    {
        var message = (sourceHost, kind, Encoding.UTF8.GetString(body.Span));
        _lastMessage = message;
        _messages.Push(message);
        _receiveSignal.Release();
        return Task.CompletedTask;
    }

    protected override Task OnPeers(string[] hostNames)
    {
        _peers.Push(hostNames);
        return Task.CompletedTask;
    }

    protected override Task OnKicked(string reason)
    {
        _kickReason = reason;
        _kickSignal.Release();
        return Task.CompletedTask;
    }

    // blocks until _server and _encryption are set — use this as the connection-ready barrier
    public async Task WaitForReady(int timeoutMs = 15000, CancellationToken cancel = default)
    {
        if (!await _readySignal.WaitAsync(timeoutMs, cancel))
            throw new TimeoutException("Timed out waiting for relay connection");
    }

    // blocks until the reconnect loop is parked on a suspension, after which it cannot connect until resumed
    public async Task WaitUntilParked(int timeoutMs = 15000)
    {
        if (!await _parkedSignal.WaitAsync(timeoutMs))
            throw new TimeoutException("Timed out waiting for the suspended relay to park");
    }

    // starts the client and waits until it has authenticated
    public async Task StartReady(CancellationToken cancel = default)
    {
        await StartAsync(CancellationToken.None);
        await WaitForReady(cancel: cancel);
    }

    // blocks until the next Peers broadcast arrives
    public Task<string[]> WaitForPeers(int timeoutMs = 15000) => _peers.Next(timeoutMs, "peers update");

    public async Task<(string Source, MessageKind Kind, string Json)> WaitForMessage(int timeoutMs = 15000)
    {
        if (!await _receiveSignal.WaitAsync(timeoutMs))
            throw new TimeoutException("Timed out waiting for message");
        return _lastMessage!.Value;
    }

    // pops the next message in arrival order — unlike WaitForMessage/_lastMessage, a burst of several
    // messages arriving before the test reads any of them is never collapsed down to just the last one.
    public Task<(string Source, MessageKind Kind, string Json)> WaitForNextMessage(int timeoutMs = 15000) =>
        _messages.Next(timeoutMs, "message");

    public async Task<string> WaitForKick(int timeoutMs = 15000)
    {
        if (!await _kickSignal.WaitAsync(timeoutMs))
            throw new TimeoutException("Timed out waiting for kick");
        return _kickReason!;
    }

    public async ValueTask DisposeAsync()
    {
        if (!_disposed.TrySet()) return;
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        try { await StopAsync(cts.Token); }
        catch { /* ignore stop errors in cleanup */ }
        Dispose();
    }
}

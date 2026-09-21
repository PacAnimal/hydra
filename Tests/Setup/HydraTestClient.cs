using System.Text;
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

    private readonly SemaphoreSlim _readySignal = new(0);
    private readonly NotificationQueue<string[]> _peers = new();
    private readonly NotificationQueue<(string Source, MessageKind Kind, string Json)> _messages = new();
    private readonly SemaphoreSlim _receiveSignal = new(0);
    private readonly SemaphoreSlim _kickSignal = new(0);

    private (string Source, MessageKind Kind, string Json)? _lastMessage;
    private string? _kickReason;

    private volatile TaskCompletionSource? _gate;
    private RelayLane _gatedLane;
    private int _held;

    public (string Source, MessageKind Kind, string Json)? LastMessage => _lastMessage;
    public string? KickReason => _kickReason;

    protected override TimeSpan ReconnectDelay => TimeSpan.Zero;

    protected override Task OnAuthenticated() { _readySignal.Release(); return Task.CompletedTask; }

    /// <summary>
    /// Parks one lane just before it encrypts, until <see cref="ReleaseLane"/>.
    ///
    /// <para>A GATE, never a delay: the claims under test are about what can overtake what, and a test that
    /// raced a big payload against a small one would be asserting that this machine happened to be fast
    /// enough. <see cref="Held"/> says when the lane is genuinely parked, so a test waits on a fact.</para>
    ///
    /// <para>Either lane, because both need parking: the bulk one to show input overtaking it, the input
    /// one to show a dropped connection failing what is queued there. Holding one says nothing about the
    /// other — that is the whole point of them being separate.</para>
    /// </summary>
    public void HoldLane(RelayLane lane)
    {
        _gatedLane = lane;
        _gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
    }

    /// <summary>How many payloads the gated lane is parked on right now.</summary>
    public int Held => Volatile.Read(ref _held);

    /// <summary>
    /// Lets the parked lane through, and STOPS GATING.
    ///
    /// <para>Clearing the gate is the load-bearing half. Leaving a completed one in place would send every
    /// later payload through the counter — increment, await an already-finished task, decrement — so
    /// <see cref="Held"/> would briefly report a payload as held that is not held at all, and the next test
    /// to wait on that would be waiting on a lie.</para>
    /// </summary>
    public void ReleaseLane() => Interlocked.Exchange(ref _gate, null)?.TrySetResult();

    protected override async ValueTask<byte[]> EncryptForSend(byte[] payload, CancellationToken cancel)
    {
        if (_gate is { } gate && MessageLane.Of(payload) == _gatedLane)
        {
            Interlocked.Increment(ref _held);
            try { await gate.Task.WaitAsync(cancel); }
            finally { Interlocked.Decrement(ref _held); }
        }

        return await base.EncryptForSend(payload, cancel);
    }

    // route the hub connection through the in-memory test server handler
    protected override void ConfigureHubUrl(HttpConnectionOptions options)
    {
        options.HttpMessageHandlerFactory = _ => _factory.Server.CreateHandler();
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
    public async Task WaitForReady(int timeoutMs = 15000)
    {
        if (!await _readySignal.WaitAsync(timeoutMs))
            throw new TimeoutException("Timed out waiting for relay connection");
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
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        try { await StopAsync(cts.Token); }
        catch { /* ignore stop errors in cleanup */ }
        Dispose();
    }
}

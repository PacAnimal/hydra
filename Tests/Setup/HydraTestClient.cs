using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using System.Text;
using Cathedral.Utils;
using Hydra.Config;
using Hydra.Relay;
using Microsoft.AspNetCore.Http.Connections.Client;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Logging;

namespace Tests.Setup;

/// <summary>
/// Test wrapper around the real RelayConnection — uses the actual Hydra relay code including RelayEncryption.
/// Use this for end-to-end tests that prove the full Hydra relay stack works as intended.
/// Use TestStyxClient instead when you need to test protocol-level edge cases.
/// </summary>
public sealed class HydraTestClient(WebApplicationFactory<global::Styx.Program>? factory, IHydraProfile profile, ILogger<RelayConnection>? log = null)
    : RelayConnection(profile, log ?? TestLog.CreateLogger<RelayConnection>(), new WorldState()), IAsyncDisposable
{
    // a master named host on the network the config describes
    public static HydraTestClient Master(WebApplicationFactory<global::Styx.Program> factory, string host, string networkConfig) =>
        new(factory, TransitionTestHelper.Profile(host, new HydraConfig { Mode = Mode.Master, NetworkConfig = networkConfig }));

    // dials the relay over real TCP, for an embedded relay listening on a port
    public static HydraTestClient OverTcp(IHydraProfile profile, ILogger<RelayConnection>? log = null) => new(null, profile, log);

    private readonly Toggle _disposed = new();
    public bool Disposed => _disposed;

    private readonly SemaphoreSlim _readySignal = new(0);
    private readonly NotificationQueue<string[]> _peers = new();
    private readonly NotificationQueue<ReceivedMessage> _messages = new();
    private readonly NotificationQueue<string> _kicks = new();
    private readonly SemaphoreSlim _parkedSignal = new(0);
    private readonly ConcurrentQueue<Socket> _connectorSockets = new();

    private volatile LaneGate? _gate;

    protected override TimeSpan ReconnectDelay => TimeSpan.Zero;

    protected override Task OnAuthenticated() { _readySignal.Release(); return Task.CompletedTask; }

    protected override void OnParkedUntilResumed() => _parkedSignal.Release();

    // parks the lane just before it encrypts, until ReleaseLane; WaitUntilLaneHeld says when it is parked
    public void HoldLane(RelayLane lane) => _gate = new LaneGate(lane);

    public async Task WaitUntilLaneHeld()
    {
        var gate = _gate ?? throw new InvalidOperationException("no lane is held");
        try { await gate.Held.Task.WaitAsync(TestBound.Bound); }
        catch (TimeoutException) { throw new TimeoutException($"Timed out waiting for the {gate.Lane} lane to park"); }
    }

    // lets the parked lane through and stops gating, so later payloads pass untouched
    public void ReleaseLane() => Interlocked.Exchange(ref _gate, null)?.Open.Release();

    // parks the lane behind a reliable blocker, enqueues behind it, then closes with a sentinel and lets it all go
    public async Task BehindParkedLane(RelayLane lane, string target, byte[] blocker, Action enqueue)
    {
        HoldLane(lane);
        var parked = SendReliableAsync([target], blocker).AsTask();
        await WaitUntilLaneHeld();
        enqueue();
        Send([target], TestMessages.Sentinel());
        ReleaseLane();
        await parked.WaitAsync(TestBound.Bound);
    }

    protected override async ValueTask<byte[]> EncryptForSend(byte[] payload, CancellationToken cancel)
    {
        if (_gate is { } gate && MessageLane.Of(payload) == gate.Lane)
        {
            gate.Held.TrySetResult();
            await gate.Open.Passed(cancel);
        }

        return await base.EncryptForSend(payload, cancel);
    }

    private sealed class LaneGate(RelayLane lane)
    {
        public RelayLane Lane { get; } = lane;
        public TaskCompletionSource Held { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public Gate Open { get; } = new(held: true);
    }

    // route the hub connection through the in-memory test server handler, when there is one
    protected override void ConfigureHubUrl(HttpConnectionOptions options)
    {
        if (factory != null) options.UseTestServer(factory.Server);
        else base.ConfigureHubUrl(options);
    }

    protected override async Task<Socket> ConnectRelaySocket(DnsEndPoint target, CancellationToken cancel)
    {
        var socket = await base.ConnectRelaySocket(target, cancel);
        _connectorSockets.Enqueue(socket);
        return socket;
    }

    // closes every socket the relay's connector has opened so far
    public void CloseConnectorSockets()
    {
        while (_connectorSockets.TryDequeue(out var socket)) socket.Dispose();
    }

    protected override Task OnReceive(string sourceHost, MessageKind kind, ReadOnlyMemory<byte> body)
    {
        _messages.Push(new ReceivedMessage(sourceHost, kind, Encoding.UTF8.GetString(body.Span)));
        return Task.CompletedTask;
    }

    protected override Task OnPeers(string[] hostNames)
    {
        _peers.Push(hostNames);
        return Task.CompletedTask;
    }

    protected override Task OnKicked(string reason)
    {
        _kicks.Push(reason);
        return Task.CompletedTask;
    }

    // blocks until _server and _encryption are set — use this as the connection-ready barrier
    public async Task WaitForReady(CancellationToken cancel = default)
    {
        if (!await _readySignal.WaitAsync(TestBound.Bound, cancel))
            throw new TimeoutException("Timed out waiting for relay connection");
    }

    // blocks until the reconnect loop is parked on a suspension, after which it cannot connect until resumed
    public async Task WaitUntilParked()
    {
        if (!await _parkedSignal.WaitAsync(TestBound.Bound))
            throw new TimeoutException("Timed out waiting for the suspended relay to park");
    }

    // the client started and authenticated, or disposed and the failure thrown
    public static async Task<HydraTestClient> Started(HydraTestClient client)
    {
        try
        {
            await client.StartReady();
            return client;
        }
        catch
        {
            await client.DisposeAsync();
            throw;
        }
    }

    // starts the client and waits until it has authenticated
    public async Task StartReady(CancellationToken cancel = default)
    {
        await StartAsync(CancellationToken.None);
        await WaitForReady(cancel: cancel);
    }

    // each wait takes the next notification in arrival order, so two before a wait are never read as one
    public Task<string[]> WaitForPeers() => _peers.Next("peers update");

    public Task<ReceivedMessage> WaitForMessage(string what = "message") => _messages.Next(what);

    public Task<string> WaitForKick() => _kicks.Next("kick");

    // the body of the next message of this kind, skipping the rest
    public async Task<string> WaitForMessage(MessageKind kind)
    {
        while (true)
        {
            var message = await WaitForMessage($"{kind} message");
            if (message.Kind == kind) return message.Json;
        }
    }

    // every frame before TestMessages.Sentinel, so a lost message fails on the assertion rather than a timeout
    public async Task<List<ReceivedMessage>> ReadUntilSentinel()
    {
        var frames = new List<ReceivedMessage>();
        while (true)
        {
            var frame = await WaitForMessage("sentinel");
            if (frame.Kind == TestMessages.SentinelKind) return frames;
            frames.Add(frame);
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (!_disposed.TrySet()) return;
        using var cts = new CancellationTokenSource(Bound);
        try { await StopAsync(cts.Token); }
        catch { /* ignore stop errors in cleanup */ }
        Dispose();
    }
}

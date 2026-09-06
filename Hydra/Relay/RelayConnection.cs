using Cathedral.Utils;
using Common;
using Common.DTO;
using Common.Interfaces;
using Hydra.Config;
using Microsoft.AspNetCore.Http.Connections.Client;
using Microsoft.AspNetCore.SignalR.Client;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using System.Net.Sockets;
using System.Threading.Channels;
using TypedSignalR.Client;
using StyxConstants = Styx.Constants;

namespace Hydra.Relay;

public class RelayConnection(IHydraProfile profile, ILogger<RelayConnection> log, IWorldState peerState)
    : SimpleHostedService(log), IStyxClient, IRelaySender
{
    private IStyxServer? _server;
    private RelayEncryption? _encryption;
    private readonly Lock _connectionLock = new();
    private CancellationTokenSource? _connectionCancellation;
    private CancellationTokenSource? _reconnectDelayCancellation;
    private bool _connectionIterationActive;
    private bool _connectionSuspended;
    private long _fastReconnectUntil;
    private long _wakeStateVersion;
    private long _latestSleepGeneration;
    private long _latestWakeGeneration;
    private long _completedWakeGeneration;
    private TaskCompletionSource _resumeConnection = CompletedSignal();
    private TaskCompletionSource? _suspensionComplete;
    private readonly Lock _sendOrderLock = new();
    private MovementBatch? _openMovementBatch;

    // One ordered outbound queue preserves key/control ordering. Bulk producers use SendReliableAsync and
    // wait until their item has actually left the queue, so a file compressor cannot retain thousands of
    // large payloads. Mouse traffic is capped by InputRouter and coalesced again at write time below.
    // The queue is deliberately unbounded: after bulk traffic gained backpressure, the remaining producers
    // are small control/input messages and dropping an arbitrary oldest item could lose KeyUp/LeaveScreen.
    private readonly Channel<OutboundMessage> _sendQueue =
        Channel.CreateUnbounded<OutboundMessage>(
            new UnboundedChannelOptions { SingleReader = true, AllowSynchronousContinuations = false });

    protected virtual TimeSpan ReconnectDelay => TimeSpan.FromSeconds(Constants.ReconnectDelaySeconds);
    protected virtual TimeSpan SystemWakeReconnectDelay => TimeSpan.FromSeconds(1);
    protected virtual TimeSpan EarlySystemWakeReconnectWindow => TimeSpan.FromSeconds(30);
    protected virtual TimeSpan SystemWakeReconnectGracePeriod => TimeSpan.FromSeconds(5);

    // RR5: ±25% jitter so peers that all dropped at once (e.g. a relay restart) don't reconnect in lockstep
    private static TimeSpan WithJitter(TimeSpan baseDelay)
    {
        var offsetMs = (Random.Shared.NextDouble() * 2 - 1) * baseDelay.TotalMilliseconds * 0.25;
        return baseDelay + TimeSpan.FromMilliseconds(offsetMs);
    }

    // IRelaySender
    public bool IsConnected => _server != null;
    public event Func<string[], Task>? PeersChanged;
    public event Func<string, MessageKind, ReadOnlyMemory<byte>, Task>? MessageReceived;
    public event Func<Task>? Disconnected;

    public void Send(string[] targetHosts, byte[] payload)
    {
        OnSent(targetHosts, payload);
        if (_server == null || _encryption == null) return;
        lock (_sendOrderLock)
        {
            if (payload.Length > 0 && payload[0] == (byte)MessageKind.MouseMove)
            {
                if (_openMovementBatch?.TryAppendAbsolute(targetHosts, payload) == true) return;
                var movement = MovementBatch.CreateAbsolute(targetHosts, payload);
                _openMovementBatch = movement;
                _sendQueue.Writer.TryWrite(new OutboundMessage(targetHosts, payload, null, movement, CancellationToken.None));
                return;
            }

            // Fallback for a delta that reaches the generic Send() instead of SendMouseDelta (e.g. an
            // IRelaySender decorator that forwards Send but not SendMouseDelta) — decodes once here so it
            // still coalesces instead of silently losing batching. InputRouter, the actual hot path, never
            // hits this: it calls SendMouseDelta directly and never encodes/decodes at all to accumulate.
            if (payload.Length > 0 && payload[0] == (byte)MessageKind.MouseMoveDelta && TryDecodeDelta(payload, out var dx, out var dy))
            {
                if (_openMovementBatch?.TryAppendDelta(targetHosts, dx, dy) == true) return;
                var movement = MovementBatch.CreateDelta(targetHosts, dx, dy);
                _openMovementBatch = movement;
                _sendQueue.Writer.TryWrite(new OutboundMessage(targetHosts, payload, null, movement, CancellationToken.None));
                return;
            }

            _openMovementBatch = null;
            _sendQueue.Writer.TryWrite(new OutboundMessage(targetHosts, payload, null, null, CancellationToken.None));
        }
    }

    // The caller already has dx/dy as ints (see IRelaySender.SendMouseDelta) — accumulating them here
    // costs one addition per event instead of a JSON decode, and Snapshot() encodes only once, right
    // before a batch actually goes out.
    public void SendMouseDelta(string[] targetHosts, int dx, int dy)
    {
        OnSent(targetHosts, MessageSerializer.Encode(MessageKind.MouseMoveDelta, new MouseMoveDeltaMessage(dx, dy)));
        if (_server == null || _encryption == null) return;
        lock (_sendOrderLock)
        {
            if (_openMovementBatch?.TryAppendDelta(targetHosts, dx, dy) == true) return;
            var movement = MovementBatch.CreateDelta(targetHosts, dx, dy);
            _openMovementBatch = movement;
            _sendQueue.Writer.TryWrite(new OutboundMessage(targetHosts, [], null, movement, CancellationToken.None));
        }
    }

    public ValueTask SuspendConnectionAsync(CancellationToken cancel = default) =>
        SuspendConnectionCoreAsync(null, cancel);

    public ValueTask SuspendForSystemSleepAsync(long generation, CancellationToken cancel = default) =>
        SuspendConnectionCoreAsync(generation, cancel);

    private async ValueTask SuspendConnectionCoreAsync(long? generation, CancellationToken cancel)
    {
        Task suspension;
        CancellationTokenSource? connection;
        lock (_connectionLock)
        {
            if (generation is { } sleepGeneration)
            {
                if (sleepGeneration < _latestSleepGeneration
                    || sleepGeneration <= _completedWakeGeneration)
                    return;
                _latestSleepGeneration = sleepGeneration;
            }

            if (!_connectionSuspended)
            {
                _connectionSuspended = true;
                _resumeConnection = NewSignal();
                _fastReconnectUntil = 0;
            }

            if (!_connectionIterationActive) return;
            _suspensionComplete ??= NewSignal();
            suspension = _suspensionComplete.Task;
            connection = _connectionCancellation;
        }

        try
        {
            if (connection != null)
                await connection.CancelAsync().WaitAsync(cancel).ConfigureAwait(false);
        }
        catch (ObjectDisposedException) { }
        await suspension.WaitAsync(cancel).ConfigureAwait(false);
    }

    public void ResumeConnection()
    {
        TaskCompletionSource resume;
        lock (_connectionLock)
        {
            if (!_connectionSuspended) return;
            _connectionSuspended = false;
            resume = _resumeConnection;
        }
        resume.TrySetResult();
    }

    public void BeginSystemWake(long generation)
    {
        ApplySystemWake(generation, completed: false);
    }

    public void CompleteSystemWake(long generation)
    {
        ApplySystemWake(generation, completed: true);
    }

    private void ApplySystemWake(long generation, bool completed)
    {
        TaskCompletionSource? resume = null;
        CancellationTokenSource? retryDelay;
        lock (_connectionLock)
        {
            if (generation < _latestSleepGeneration
                || generation < _latestWakeGeneration
                || !completed && generation <= _completedWakeGeneration)
                return;

            _latestWakeGeneration = generation;
            if (completed) _completedWakeGeneration = generation;
            var window = completed ? SystemWakeReconnectGracePeriod : EarlySystemWakeReconnectWindow;
            _fastReconnectUntil = Stopwatch.GetTimestamp()
                + (long)(window.TotalSeconds * Stopwatch.Frequency);
            _wakeStateVersion++;

            if (_connectionSuspended)
            {
                _connectionSuspended = false;
                resume = _resumeConnection;
            }
            retryDelay = _reconnectDelayCancellation;
        }

        resume?.TrySetResult();
        try { retryDelay?.Cancel(); }
        catch (ObjectDisposedException) { }
    }

    public async ValueTask SendReliableAsync(string[] targetHosts, byte[] payload, CancellationToken cancel = default)
    {
        OnSent(targetHosts, payload);
        if (_server == null || _encryption == null)
            throw new InvalidOperationException("Relay is not connected");

        var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        lock (_sendOrderLock)
        {
            _openMovementBatch = null;
            if (!_sendQueue.Writer.TryWrite(new OutboundMessage(targetHosts, payload, completion, null, cancel)))
                throw new InvalidOperationException("Relay send queue is closed");
        }

        using var registration = cancel.Register(() => completion.TrySetCanceled(cancel));
        await completion.Task.ConfigureAwait(false);
    }

    protected virtual void OnSent(string[] targetHosts, byte[] payload) { }

    // IStyxClient
    public async Task Receive(string sourceHost, string sourceIp, byte[] payload)
    {
        if (_encryption == null) return;

        byte[] decrypted;
        try
        {
            decrypted = await _encryption.Decrypt(sourceHost, payload, log);
        }
        catch (Exception ex)
        {
            log.LogWarning(ex, "Could not decrypt message from {SourceHost} — discarding (wrong key or malicious sender)", sourceHost);
            return;
        }

        try
        {
            var decoded = MessageSerializer.Decode(decrypted);
            if (log.IsEnabled(LogLevel.Trace))
                log.LogTrace("Received {Kind} from {SourceHost} ({Bytes} bytes)", decoded.Kind, sourceHost, payload.Length);
            await OnReceive(sourceHost, decoded.Kind, decoded.Bytes);
        }
        catch (Exception ex)
        {
            log.LogWarning(ex, "Failed to decode message from {SourceHost}", sourceHost);
        }
    }

    public async Task Kicked(string reason)
    {
        log.LogWarning("Kicked from relay: {Reason}", reason);
        await OnKicked(reason);
    }

    public async Task Peers(string[] hostNames)
    {
        log.LogInformation("Peers online: {Peers}", hostNames.Length == 0 ? "(none)" : string.Join(", ", hostNames));
        await OnPeers(hostNames);
    }

    // override in subclasses (e.g. tests, slave mode)
    protected virtual async Task OnReceive(string sourceHost, MessageKind kind, ReadOnlyMemory<byte> body)
    {
        if (MessageReceived != null) await MessageReceived(sourceHost, kind, body);
        else await ValueTask.CompletedTask;
    }

    protected virtual async Task OnPeers(string[] hostNames)
    {
        if (PeersChanged != null) await PeersChanged(hostNames);
        else await ValueTask.CompletedTask;
    }

    protected virtual Task OnKicked(string reason) => Task.CompletedTask;
    // fires after _server and _encryption are set — guaranteed connection-ready signal
    protected virtual Task OnAuthenticated() => Task.CompletedTask;
    // per-connection cancellation token: cancels when this connection drops. Valid only during
    // OnAuthenticated (the source CTS is disposed once the connection loop unwinds, before OnDisconnected).
    protected CancellationToken ConnectionToken { get; private set; }
    // fires when a live connection drops (not on auth failure or clean shutdown)
    protected virtual Task OnDisconnected() => Task.CompletedTask;

    // override in tests to inject the in-memory handler; production default sets NoDelay
    protected virtual void ConfigureHubUrl(HttpConnectionOptions options)
    {
        options.HttpMessageHandlerFactory = _ => new SocketsHttpHandler
        {
            ConnectCallback = async (ctx, cancel) =>
            {
                var socket = new Socket(SocketType.Stream, ProtocolType.Tcp) { NoDelay = true };
                await socket.ConnectAsync(ctx.DnsEndPoint, cancel);
                return new NetworkStream(socket, ownsSocket: true);
            }
        };
    }

    protected override async Task Execute(CancellationToken cancel)
    {
        if (profile.NetworkConfig == null) return;

        NetworkConfig netConfig;
        try
        {
            netConfig = NetworkConfig.Parse(profile.NetworkConfig);
        }
        catch (Exception ex)
        {
            log.LogError(ex, "Failed to parse NetworkConfig — relay disabled");
            return;
        }

        var hostName = profile.Name;
        log.LogInformation("Starting relay connection to {Server} as {HostName}", netConfig.StyxServer, hostName);

        while (!cancel.IsCancellationRequested)
        {
            await WaitUntilConnectionResumed(cancel).ConfigureAwait(false);
            if (!TryBeginConnectionIteration()) continue;
            TimeSpan? reconnectDelay = null;
            try
            {
                await Connect(netConfig, hostName, cancel);
            }
            catch (OperationCanceledException) when (cancel.IsCancellationRequested)
            {
                break;
            }
            catch (OperationCanceledException) when (IsConnectionSuspended())
            {
                log.LogInformation("Relay connection suspended for system sleep");
            }
            catch (OperationCanceledException)
            {
                reconnectDelay = CurrentReconnectDelay();
                log.LogWarning("Relay connection lost — retrying in {ReconnectDelay}s", reconnectDelay.Value.TotalSeconds);
            }
            catch (HttpRequestException ex)
            {
                reconnectDelay = CurrentReconnectDelay();
                log.LogWarning("Relay connection failed — retrying in {ReconnectDelay}s: {Message}", reconnectDelay.Value.TotalSeconds, ex.InnerException?.Message ?? ex.Message);
            }
            catch (Exception ex)
            {
                reconnectDelay = CurrentReconnectDelay();
                log.LogError(ex, "Relay connection failed — retrying in {ReconnectDelay}s", reconnectDelay.Value.TotalSeconds);
            }
            finally
            {
                var wasConnected = _server != null;
                _server = null;
                _encryption = null;
                while (TryReadQueued(out var stale))
                    stale.Completion?.TrySetException(new IOException("Relay connection lost before message was sent"));
                if (wasConnected)
                {
                    // guard the disconnect callbacks: a throw here would escape Execute, and because the
                    // base SimpleHostedService has no exceptionLoopTime it would permanently kill the
                    // reconnect loop (silent, until process restart). Log and keep reconnecting instead.
                    try
                    {
                        await OnDisconnected();
                        if (Disconnected != null) await Disconnected();
                    }
                    catch (OperationCanceledException) when (cancel.IsCancellationRequested) { }
                    catch (Exception ex)
                    {
                        log.LogError(ex, "Error handling relay disconnect — continuing to reconnect");
                    }
                }
                EndConnectionIteration();
            }

            if (!cancel.IsCancellationRequested && !IsConnectionSuspended())
            {
                var (delay, wakeStateVersion) = CurrentReconnectDelayState(reconnectDelay);
                await DelayBeforeReconnect(WithJitter(delay), wakeStateVersion, cancel).ConfigureAwait(false);
            }
        }
    }

    protected TimeSpan CurrentReconnectDelay()
        => CurrentReconnectDelayState().Delay;

    protected long WakeStateVersion
    {
        get
        {
            lock (_connectionLock) return _wakeStateVersion;
        }
    }

    private (TimeSpan Delay, long WakeStateVersion) CurrentReconnectDelayState(TimeSpan? preferred = null)
    {
        lock (_connectionLock)
        {
            var delay = Stopwatch.GetTimestamp() < _fastReconnectUntil
                ? SystemWakeReconnectDelay
                : preferred ?? ReconnectDelay;
            return (delay, _wakeStateVersion);
        }
    }

    protected async Task DelayBeforeReconnect(
        TimeSpan delay,
        long observedWakeStateVersion,
        CancellationToken cancel)
    {
        using var delayCancellation = CancellationTokenSource.CreateLinkedTokenSource(cancel);
        lock (_connectionLock)
        {
            if (_wakeStateVersion != observedWakeStateVersion) return;
            _reconnectDelayCancellation = delayCancellation;
        }

        try
        {
            await Task.Delay(delay, delayCancellation.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!cancel.IsCancellationRequested) { }
        finally
        {
            lock (_connectionLock)
                if (ReferenceEquals(_reconnectDelayCancellation, delayCancellation))
                    _reconnectDelayCancellation = null;
        }
    }

    private async Task WaitUntilConnectionResumed(CancellationToken cancel)
    {
        Task resume;
        lock (_connectionLock) resume = _resumeConnection.Task;
        await resume.WaitAsync(cancel).ConfigureAwait(false);
    }

    private bool TryBeginConnectionIteration()
    {
        lock (_connectionLock)
        {
            if (_connectionSuspended) return false;
            _connectionIterationActive = true;
            return true;
        }
    }

    private bool IsConnectionSuspended()
    {
        lock (_connectionLock) return _connectionSuspended;
    }

    protected bool ConnectionSuspended
    {
        get
        {
            lock (_connectionLock) return _connectionSuspended;
        }
    }

    private void EndConnectionIteration()
    {
        TaskCompletionSource? complete;
        lock (_connectionLock)
        {
            _connectionIterationActive = false;
            complete = _suspensionComplete;
            _suspensionComplete = null;
        }
        complete?.TrySetResult();
    }

    private static TaskCompletionSource NewSignal() =>
        new(TaskCreationOptions.RunContinuationsAsynchronously);

    private static TaskCompletionSource CompletedSignal()
    {
        var signal = NewSignal();
        signal.SetResult();
        return signal;
    }

    private async Task Connect(NetworkConfig netConfig, string hostName, CancellationToken cancel)
    {
        using var disco = CancellationTokenSource.CreateLinkedTokenSource(cancel);
        using var connectionScope = new ConnectionCancellationScope(this, disco);

        await using var con = new HubConnectionBuilder()
            .WithUrl($"{netConfig.StyxServer}/relay", ConfigureHubUrl)
            .WithKeepAliveInterval(TimeSpan.FromSeconds(StyxConstants.KeepAliveSeconds))
            .WithServerTimeout(TimeSpan.FromSeconds(StyxConstants.ClientTimeoutSeconds))
            .AddMessagePackProtocol()
            .Build();

        // ReSharper disable once AccessToDisposedClosure
        con.Closed += async _ =>
        {
            try { await disco.CancelAsync(); }
            catch (ObjectDisposedException) { }
        };

        await con.StartAsync(disco.Token);
        log.LogInformation("Connected to Styx relay");

        var server = con.CreateHubProxy<IStyxServer>(cancellationToken: disco.Token);
        using var reg = con.Register<IStyxClient>(this);

        // set before Authenticate so messages arriving during the auth handshake aren't dropped:
        // Styx broadcasts Peers (triggering MasterConfig from master) before returning Authenticated=true,
        // so _encryption must be ready to decrypt that incoming message
        _encryption = new RelayEncryption(netConfig.EncryptionKey, peerState);
        _server = server;

        // RR6: bound the auth round-trip so a server that accepts the socket then stalls the handshake
        // doesn't hang the connect attempt — WaitAsync surfaces a timeout/cancel to the reconnect loop.
        var response = await server.Authenticate(new RelayLogin
        {
            Authorization = netConfig.Authorization,
            HostName = hostName
        }).WaitAsync(TimeSpan.FromSeconds(Constants.AuthTimeoutSeconds), disco.Token);

        if (!response.Authenticated)
        {
            _server = null;
            _encryption = null;
            log.LogError("Relay authentication failed: {Message}", response.Message);
            return;
        }

        log.LogInformation("Authenticated on relay as {HostName}", hostName);
        // R5: per-connection token (cancels when this connection drops), NOT the app-lifetime token — so
        // awaiters like WaitForAccessibilityTrusted in OnAuthenticated unwind on a drop and reconnect.
        ConnectionToken = disco.Token;
        await OnAuthenticated();

        // drain outbound queue until the connection drops
        while (true)
        {
            if (!await _sendQueue.Reader.WaitToReadAsync(disco.Token)) break;
            if (!TryReadQueued(out var item)) continue;

            if (item.Cancel.IsCancellationRequested || item.Completion?.Task.IsCanceled == true)
            {
                item.Completion?.TrySetCanceled(item.Cancel);
                continue;
            }

            try
            {
                var encrypted = await _encryption.Encrypt(item.Payload, cancel);
                await _server.Send(item.Targets, encrypted);
                item.Completion?.TrySetResult();
            }
            catch (OperationCanceledException ex)
            {
                item.Completion?.TrySetCanceled(ex.CancellationToken);
                break;
            }
            catch (HttpRequestException ex)
            {
                item.Completion?.TrySetException(ex);
                log.LogWarning("Failed to send relay message to [{TargetHosts}]: {Message}", string.Join(", ", item.Targets), ex.InnerException?.Message ?? ex.Message);
            }
            catch (Exception ex)
            {
                item.Completion?.TrySetException(ex);
                log.LogWarning(ex, "Failed to send relay message to [{TargetHosts}]", string.Join(", ", item.Targets));
            }
        }
    }

    // unwraps a queued movement batch to its latest coalesced payload at read time, so a burst of
    // moves collapses to the freshest position/delta regardless of how much piled up while queued.
    private bool TryReadQueued(out OutboundMessage item)
    {
        if (!_sendQueue.Reader.TryRead(out item!)) return false;
        if (item.Movement != null)
        {
            lock (_sendOrderLock)
            {
                if (ReferenceEquals(_openMovementBatch, item.Movement)) _openMovementBatch = null;
                item = item with { Payload = item.Movement.Snapshot(), Movement = null };
            }
        }
        return true;
    }

    // Test-only: production code no longer decodes bytes for delta coalescing (see SendMouseDelta) —
    // this exists purely so tests can assert on the wire-encoded result of the same coalescing rules.
    internal static bool TryCoalesceMovement(byte[] current, byte[] next, out byte[] combined)
    {
        combined = current;
        if (current.Length == 0 || next.Length == 0 || current[0] != next[0]) return false;

        if (current[0] == (byte)MessageKind.MouseMove)
        {
            combined = next;
            return true;
        }

        if (current[0] != (byte)MessageKind.MouseMoveDelta) return false;
        if (!TryDecodeDelta(current, out var dx1, out var dy1) || !TryDecodeDelta(next, out var dx2, out var dy2))
            return false;

        var movement = MovementBatch.CreateDelta([], dx1, dy1);
        movement.TryAppendDelta([], dx2, dy2);
        combined = movement.Snapshot();
        return true;
    }

    private static bool TryDecodeDelta(byte[] payload, out int dx, out int dy)
    {
        dx = dy = 0;
        try
        {
            var delta = System.Text.Json.JsonSerializer.Deserialize<MouseMoveDeltaMessage>(
                payload.AsSpan(1), Cathedral.Config.SaneJson.Options);
            if (delta == null) return false;
            dx = delta.Dx;
            dy = delta.Dy;
            return true;
        }
        catch (System.Text.Json.JsonException) { return false; }
    }

    // Coalesces same-target movement queued faster than the drain loop can send it: an absolute move
    // keeps only the latest position, a delta accumulates — either way only one message crosses the
    // wire per burst instead of one per input event. Deltas arrive and stay as ints; only Snapshot()
    // ever encodes, once, right before the batch actually goes out.
    private sealed class MovementBatch
    {
        private readonly MessageKind _kind;
        private readonly string[] _targets;
        private byte[] _absolutePayload = [];
        private int _dx;
        private int _dy;

        private MovementBatch(string[] targets, MessageKind kind) => (_targets, _kind) = (targets, kind);

        internal static MovementBatch CreateAbsolute(string[] targets, byte[] payload) =>
            new(targets, MessageKind.MouseMove) { _absolutePayload = payload };

        internal static MovementBatch CreateDelta(string[] targets, int dx, int dy) =>
            new(targets, MessageKind.MouseMoveDelta) { _dx = dx, _dy = dy };

        private bool Matches(MessageKind kind, string[] targets) =>
            _kind == kind && _targets.SequenceEqual(targets);

        internal bool TryAppendAbsolute(string[] targets, byte[] payload)
        {
            if (!Matches(MessageKind.MouseMove, targets)) return false;
            _absolutePayload = payload;
            return true;
        }

        internal bool TryAppendDelta(string[] targets, int dx, int dy)
        {
            if (!Matches(MessageKind.MouseMoveDelta, targets)) return false;
            _dx = (int)Math.Clamp((long)_dx + dx, int.MinValue, int.MaxValue);
            _dy = (int)Math.Clamp((long)_dy + dy, int.MinValue, int.MaxValue);
            return true;
        }

        internal byte[] Snapshot() => _kind == MessageKind.MouseMove
            ? _absolutePayload
            : MessageSerializer.Encode(MessageKind.MouseMoveDelta, new MouseMoveDeltaMessage(_dx, _dy));
    }

    // Lets SuspendConnectionAsync cancel whichever Connect() attempt is currently in flight, wherever
    // it is in the connect/authenticate/drain sequence, without tearing down the whole reconnect loop.
    private sealed class ConnectionCancellationScope : IDisposable
    {
        private readonly RelayConnection _owner;
        private readonly CancellationTokenSource _cancellation;

        internal ConnectionCancellationScope(RelayConnection owner, CancellationTokenSource cancellation)
        {
            _owner = owner;
            _cancellation = cancellation;
            bool suspend;
            lock (_owner._connectionLock)
            {
                _owner._connectionCancellation = cancellation;
                suspend = _owner._connectionSuspended;
            }
            if (suspend) cancellation.Cancel();
        }

        public void Dispose()
        {
            lock (_owner._connectionLock)
                if (ReferenceEquals(_owner._connectionCancellation, _cancellation))
                    _owner._connectionCancellation = null;
        }
    }

    private sealed record OutboundMessage(
        string[] Targets,
        byte[] Payload,
        TaskCompletionSource? Completion,
        MovementBatch? Movement,
        CancellationToken Cancel);
}

using Cathedral.Utils;
using Common;
using Common.DTO;
using Common.Interfaces;
using Hydra.Config;
using Microsoft.AspNetCore.Http.Connections.Client;
using Microsoft.AspNetCore.SignalR.Client;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using System.Diagnostics;
using System.Net;
using System.Net.NetworkInformation;
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

    /// <summary>
    /// Key events queued behind a frame already in flight — see <see cref="KeyBundle"/>. Guarded by the same
    /// <c>_sendOrderLock</c> as the movement batch, because they are one question: what the NEXT enqueue may
    /// still be added to.
    /// </summary>
    private KeyBundle? _openKeyBundle;
    private RelayTransportSnapshot? _transport;
    private long _connectionAttempts;
    private long _messagesSent;
    private long _messagesReceived;
    private long _bytesSent;
    private long _bytesReceived;

    // TWO ordered lanes, each drained by its own loop — see MessageLane. Within a lane the order is exactly
    // what it always was; across lanes there is nothing to order, because a file chunk and a keypress are
    // unrelated.
    //
    // A lane's order is absolute because it never has two invocations in flight: TypedSignalR.Client
    // generates `Send` as InvokeCoreAsync, which completes when the HUB METHOD RETURNS, not when the frame
    // is flushed. That matters — the relay sets MaximumParallelInvocationsPerClient to 4, so a peer that
    // pipelined WOULD have its frames dispatched concurrently and could see them reordered. Check the
    // generated proxy before assuming otherwise after a package bump.
    //
    // What the split buys is that a 256 KiB chunk no longer sits in front of every keystroke behind it,
    // which on a KVM is the product.
    //
    // Bulk producers use SendReliableAsync and wait until their item has actually left the queue, so a file
    // compressor cannot retain thousands of large payloads. Mouse traffic is capped by InputRouter and
    // coalesced again at write time below.
    //
    // Both are deliberately unbounded: after bulk traffic gained backpressure, the remaining producers are
    // small control/input messages and dropping an arbitrary oldest item could lose KeyUp/LeaveScreen.
    private readonly Channel<OutboundMessage> _inputQueue = NewLane();
    private readonly Channel<OutboundMessage> _bulkQueue = NewLane();

    private static Channel<OutboundMessage> NewLane() =>
        Channel.CreateUnbounded<OutboundMessage>(
            new UnboundedChannelOptions { SingleReader = true, AllowSynchronousContinuations = false });

    private Channel<OutboundMessage> LaneFor(ReadOnlySpan<byte> payload) =>
        MessageLane.Of(payload) == RelayLane.Bulk ? _bulkQueue : _inputQueue;

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
    public RelayTransportSnapshot? Transport
    {
        get
        {
            var transport = _transport;
            return transport == null ? null : transport with
            {
                ConnectionAttempts = Interlocked.Read(ref _connectionAttempts),
                MessagesSent = Interlocked.Read(ref _messagesSent),
                MessagesReceived = Interlocked.Read(ref _messagesReceived),
                BytesSent = Interlocked.Read(ref _bytesSent),
                BytesReceived = Interlocked.Read(ref _bytesReceived)
            };
        }
    }
    public event Func<string[], Task>? PeersChanged;
    public event Func<string, MessageKind, ReadOnlyMemory<byte>, Task>? MessageReceived;
    public event Func<Task>? Disconnected;

    public void Send(string[] targetHosts, byte[] payload)
    {
        OnSent(targetHosts, payload);
        if (_server == null || _encryption == null) return;
        lock (_sendOrderLock)
        {
            // A key event may join the bundle already queued for these targets, but ONLY where every one of
            // them has said it understands a batch — an unknown kind is discarded in silence by the
            // receiver, so bundling at an older peer would lose the keys rather than fail. A lone event
            // still goes out as an ordinary KeyEvent, so nothing changes for the common case either way.
            //
            // REMOVE AFTER 2026-10-30: the EveryTargetTakesKeyBundles call goes, and the condition becomes
            // the kind check alone.
            if (payload.Length > 0 && payload[0] == (byte)MessageKind.KeyEvent && EveryTargetTakesKeyBundles(targetHosts)
                && MessageSerializer.Decode(payload).Deserialize<KeyEventMessage>() is { } keyEvent)
            {
                _openMovementBatch = null;
                if (_openKeyBundle?.TryAppend(targetHosts, keyEvent) == true) return;

                var bundle = KeyBundle.Create(targetHosts, keyEvent);
                _openKeyBundle = bundle;
                _inputQueue.Writer.TryWrite(new OutboundMessage(targetHosts, payload, null, null, bundle, CancellationToken.None));
                return;
            }

            // Anything that is not a key closes the bundle, for the reason KeyBundle's summary gives: an
            // append after the drain has read it goes nowhere at all.
            _openKeyBundle = null;

            if (payload.Length > 0 && payload[0] == (byte)MessageKind.MouseMove)
            {
                if (_openMovementBatch?.TryAppendAbsolute(targetHosts, payload) == true) return;
                var movement = MovementBatch.CreateAbsolute(targetHosts, payload);
                _openMovementBatch = movement;
                _inputQueue.Writer.TryWrite(new OutboundMessage(targetHosts, payload, null, movement, null, CancellationToken.None));
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
                _inputQueue.Writer.TryWrite(new OutboundMessage(targetHosts, payload, null, movement, null, CancellationToken.None));
                return;
            }

            // Closing the batch is what stops a move made AFTER this message coalescing with one made before
            // it — which is why a click lands where the cursor actually was.
            //
            // Closed by ANY message, bulk included. Skipping it for the bulk lane looks like free extra
            // coalescing, but the batch is cleared again the moment the input drain READS the item, so the
            // window where it would make a difference is one a test cannot open — and an optimisation
            // nothing can observe is not worth a special case in the one path that decides where clicks land.
            _openMovementBatch = null;
            LaneFor(payload).Writer.TryWrite(new OutboundMessage(targetHosts, payload, null, null, null, CancellationToken.None));
        }
    }

    /// <summary>
    /// Whether EVERY target has said it understands a key batch. All of them, because one frame goes to all
    /// of them — and a peer that never said is a peer that would drop it without a word.
    /// </summary>
    /// <remarks>
    /// <b>REMOVE AFTER 2026-10-30</b> — see <c>ScreenInfoMessage.KeyBundles</c> for everything that goes with
    /// it. After that date bundling is unconditional and this method, its call site's <c>&amp;&amp;</c>, and the
    /// capability it reads all come out.
    /// </remarks>
    private bool EveryTargetTakesKeyBundles(string[] targetHosts)
    {
        if (targetHosts.Length == 0) return false;
        foreach (var host in targetHosts)
            if (!peerState.PeerSupportsKeyBundles(host))
                return false;
        return true;
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
            _openKeyBundle = null;
            if (_openMovementBatch?.TryAppendDelta(targetHosts, dx, dy) == true) return;
            var movement = MovementBatch.CreateDelta(targetHosts, dx, dy);
            _openMovementBatch = movement;
            _inputQueue.Writer.TryWrite(new OutboundMessage(targetHosts, [], null, movement, null, CancellationToken.None));
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

    // Manual reconnect (TUI command): cancels whichever Connect() attempt is currently in flight so the
    // Execute loop's reconnect-delay-then-retry immediately kicks in, without touching suspend state.
    public bool RequestReconnect()
    {
        lock (_connectionLock)
        {
            if (_connectionCancellation == null || _connectionCancellation.IsCancellationRequested) return false;
            _connectionCancellation.Cancel();
            return true;
        }
    }

    public async ValueTask SendReliableAsync(string[] targetHosts, byte[] payload, CancellationToken cancel = default)
    {
        OnSent(targetHosts, payload);
        if (_server == null || _encryption == null)
            throw new InvalidOperationException("Relay is not connected");

        var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        // By KIND, never by which method was called: FileTransferService streams chunks through here and
        // RemoteManagementService sends a control request through here, and those belong on different lanes.
        var lane = LaneFor(payload);
        lock (_sendOrderLock)
        {
            _openMovementBatch = null;
            _openKeyBundle = null;
            if (!lane.Writer.TryWrite(new OutboundMessage(targetHosts, payload, completion, null, null, cancel)))
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

        Interlocked.Increment(ref _messagesReceived);
        Interlocked.Add(ref _bytesReceived, payload.LongLength);

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
                var socket = await RelaySocketConnector.ConnectAsync(ctx.DnsEndPoint, cancel);
                CaptureTransport(socket, ctx.DnsEndPoint);
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
            Interlocked.Increment(ref _connectionAttempts);
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
                log.LogWarning("Relay connection lost — retrying in {ReconnectDelay}s", CurrentReconnectDelay().TotalSeconds);
            }
            catch (HttpRequestException ex)
            {
                log.LogWarning("Relay connection failed — retrying in {ReconnectDelay}s: {Message}", CurrentReconnectDelay().TotalSeconds, ex.InnerException?.Message ?? ex.Message);
            }
            catch (Exception ex)
            {
                log.LogError(ex, "Relay connection failed — retrying in {ReconnectDelay}s", CurrentReconnectDelay().TotalSeconds);
            }
            finally
            {
                var wasConnected = _server != null;
                _server = null;
                _encryption = null;
                _transport = null;
                // BOTH lanes. A caller parked in SendReliableAsync is waiting on a completion that only this
                // drain will ever set, so a lane left unemptied is a caller hung until the process exits —
                // and the bulk lane is the one whose callers actually await.
                FailQueued(_inputQueue.Reader);
                FailQueued(_bulkQueue.Reader);
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
                // Re-evaluate after disconnect callbacks: they can outlive the wake grace window, and a
                // delay observed in the catch block must not keep the fast cadence alive after expiry.
                var (delay, wakeStateVersion) = CurrentReconnectDelayState();
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

    private (TimeSpan Delay, long WakeStateVersion) CurrentReconnectDelayState()
    {
        lock (_connectionLock)
        {
            var delay = Stopwatch.GetTimestamp() < _fastReconnectUntil
                ? SystemWakeReconnectDelay
                : ReconnectDelay;
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

        // Drain both lanes until the connection drops. Concurrently, which is the entire point: a chunk
        // being encrypted and sent no longer holds up the keystroke behind it.
        //
        // CONCURRENT ENCRYPTION IS SAFE, AND THE REASON IS NARROWER THAN IT LOOKS. RelayEncryption draws ONE
        // salt per connection (_localKey) and reuses it for every message, so the AES-GCM key is CONSTANT for
        // the life of this connection — what is per-message is the nonce, not the salt. Under a fixed key,
        // nonce uniqueness is the only thing between us and catastrophe: GCM reuse leaks the XOR of the two
        // plaintexts and hands out forgeries.
        //
        // Iv96.Next gives it. Its low 48 bits are Interlocked.Increment on a PROCESS-wide counter, so two
        // lanes cannot draw the same value however they interleave — and process-wide is the right scope,
        // because it holds across two RelayEncryption instances as well as across these two loops. The high
        // 48 bits are a timestamp and may well be identical; the counter is what separates them. Everything
        // else SimpleAes.Encrypt touches is immutable, and it builds a fresh AesGcm per call.
        //
        // Either lane exiting ends the connection, exactly as the single loop's `break` used to: whatever
        // stopped it (a cancelled send, a dead socket) has stopped the other too or is about to.
        await Task.WhenAll(
            Drain(_inputQueue.Reader, disco),
            Drain(_bulkQueue.Reader, disco));
    }

    /// <summary>
    /// Drains one lane until the connection drops.
    ///
    /// <para><b>Everything here runs on the CONNECTION's token, never the app's.</b> Suspending or dropping
    /// a connection cancels <c>disco</c> and then WAITS for this iteration to finish
    /// (<c>SuspendConnectionCoreAsync</c>), so a drain parked on work that only the app-lifetime token can
    /// cancel deadlocks the suspend — the reconnect, the sleep handler and a clean shutdown all go through
    /// it. Encryption is fast enough that this never showed in the field; it is still the wrong token, and a
    /// test that holds a lane still finds it immediately.</para>
    /// </summary>
    private async Task Drain(ChannelReader<OutboundMessage> reader, CancellationTokenSource disco)
    {
        try
        {
            while (true)
            {
                if (!await reader.WaitToReadAsync(disco.Token)) break;
                if (!TryReadQueued(reader, out var item)) continue;

                if (item.Cancel.IsCancellationRequested || item.Completion?.Task.IsCanceled == true)
                {
                    item.Completion?.TrySetCanceled(item.Cancel);
                    continue;
                }

                try
                {
                    var encrypted = await EncryptForSend(item.Payload, disco.Token);
                    await _server!.Send(item.Targets, encrypted);
                    Interlocked.Increment(ref _messagesSent);
                    Interlocked.Add(ref _bytesSent, encrypted.LongLength);
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
        finally
        {
            // One lane stopping means this connection is over, so wake the other rather than leaving it
            // parked in WaitToReadAsync until something else happens to cancel it.
            try { await disco.CancelAsync(); }
            catch (ObjectDisposedException) { /* the connect method already unwound */ }
        }

        // NOT caught. The cancellation has to reach Execute, which is where a drop is CLASSIFIED — lost,
        // suspended for sleep, or shutting down — and where the only log line a master ever writes about
        // it comes from. Swallowing it here left an idle relay drop, which for a KVM is the common one,
        // producing no output at all: the client reconnected in silence.
    }

    /// <summary>
    /// Encrypts one queued payload, on its own lane's drain loop.
    ///
    /// <para>Virtual for ONE reason: a test needs to hold one lane still while the other runs, and that is
    /// the property the whole split exists for. Asserting it by racing a big payload against a small one
    /// would be timing dressed as a test — on a loaded machine it would eventually lie in both directions.
    /// Production has exactly one implementation, and this is it.</para>
    /// </summary>
    protected virtual ValueTask<byte[]> EncryptForSend(byte[] payload, CancellationToken cancel) =>
        _encryption!.Encrypt(payload, cancel);

    /// <summary>Fails everything still queued on one lane, so nobody waits on a send this connection will never make.</summary>
    private void FailQueued(ChannelReader<OutboundMessage> reader)
    {
        while (TryReadQueued(reader, out var stale))
            stale.Completion?.TrySetException(new IOException("Relay connection lost before message was sent"));
    }

    // unwraps a queued movement batch to its latest coalesced payload at read time, so a burst of
    // moves collapses to the freshest position/delta regardless of how much piled up while queued.
    private bool TryReadQueued(ChannelReader<OutboundMessage> reader, out OutboundMessage item)
    {
        if (!reader.TryRead(out item!)) return false;
        if (item.Movement != null)
        {
            lock (_sendOrderLock)
            {
                if (ReferenceEquals(_openMovementBatch, item.Movement)) _openMovementBatch = null;
                item = item with { Payload = item.Movement.Snapshot(), Movement = null };
            }
        }
        else if (item.Keys != null)
        {
            lock (_sendOrderLock)
            {
                // BY REFERENCE, and the clear is what stops a later append landing in a bundle that has
                // already been read — which would lose those keys with nothing to show for it.
                if (ReferenceEquals(_openKeyBundle, item.Keys)) _openKeyBundle = null;
                item = item with { Payload = item.Keys.Snapshot(), Keys = null };
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
    /// <summary>
    /// Key events queued for one target while an earlier frame is still in flight, kept IN ORDER.
    ///
    /// <para><b>This is the opposite of <see cref="MovementBatch"/> and the difference is the whole point.</b>
    /// A batch of moves collapses to the latest position because the earlier ones are no longer true. Every
    /// key event stays true: a KeyDown and its KeyUp are two separate facts, and dropping either leaves a key
    /// held down on somebody else's machine. So this appends and never replaces.</para>
    ///
    /// <para><b>The loss to fear is an append to a bundle nobody will read again.</b> Appending does not
    /// enqueue anything — it mutates an object an already-queued item points at — so the moment the drain
    /// has taken its <see cref="Snapshot"/>, a further append would vanish without trace. That is why
    /// <c>_openKeyBundle</c> is cleared under <c>_sendOrderLock</c> both by the drain (by reference) and by
    /// every other enqueue, exactly as the movement batch is. It is not a theoretical hazard: deleting that
    /// clear silently loses a mouse move, which is what <c>AMessageBetweenTwoMouseMovesKeepsThemApart</c>
    /// exists to catch.</para>
    /// </summary>
    private sealed class KeyBundle
    {
        /// <summary>
        /// How many events one frame may carry. A bound so a stalled relay cannot grow one frame without
        /// limit — not a discard: <c>TryAppend</c> refuses when full and the caller enqueues a NEW bundle,
        /// so the event that did not fit still goes, still in order.
        /// </summary>
        internal const int Capacity = 64;

        private readonly string[] _targets;
        private readonly List<KeyEventMessage> _events = [];

        private KeyBundle(string[] targets) => _targets = targets;

        internal static KeyBundle Create(string[] targets, KeyEventMessage first)
        {
            var bundle = new KeyBundle(targets);
            bundle._events.Add(first);
            return bundle;
        }

        internal bool TryAppend(string[] targets, KeyEventMessage message)
        {
            if (_events.Count >= Capacity || !_targets.SequenceEqual(targets)) return false;
            _events.Add(message);
            return true;
        }

        /// <summary>
        /// The frame to send. ONE event goes as an ordinary <c>KeyEvent</c>, so the common case puts nothing
        /// new on the wire and a peer only ever meets a batch it asked for.
        /// </summary>
        internal byte[] Snapshot() => _events.Count == 1
            ? MessageSerializer.Encode(MessageKind.KeyEvent, _events[0])
            : MessageSerializer.Encode(MessageKind.KeyEventBatch, new KeyEventBatchMessage([.. _events]));
    }

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

    private void CaptureTransport(Socket socket, DnsEndPoint target)
    {
        if (socket.LocalEndPoint is not IPEndPoint local || socket.RemoteEndPoint is not IPEndPoint remote) return;
        var network = FindInterface(local.Address);
        _transport = new RelayTransportSnapshot(
            network?.Name ?? "unknown",
            DescribeInterface(network),
            local.Address.ToString(),
            local.Port,
            target.Host,
            remote.Address.ToString(),
            remote.Port,
            DateTimeOffset.UtcNow,
            Interlocked.Read(ref _connectionAttempts),
            Interlocked.Read(ref _messagesSent),
            Interlocked.Read(ref _messagesReceived),
            Interlocked.Read(ref _bytesSent),
            Interlocked.Read(ref _bytesReceived));
    }

    private static NetworkInterface? FindInterface(IPAddress address)
    {
        var normalized = address.IsIPv4MappedToIPv6 ? address.MapToIPv4() : address;
        try
        {
            return NetworkInterface.GetAllNetworkInterfaces().FirstOrDefault(network =>
                network.GetIPProperties().UnicastAddresses.Any(unicast =>
                {
                    var candidate = unicast.Address.IsIPv4MappedToIPv6 ? unicast.Address.MapToIPv4() : unicast.Address;
                    return candidate.Equals(normalized);
                }));
        }
        catch (NetworkInformationException)
        {
            return null;
        }
    }

    private static string DescribeInterface(NetworkInterface? network) => network?.NetworkInterfaceType switch
    {
        NetworkInterfaceType.Wireless80211 => "Wi-Fi",
        NetworkInterfaceType.Ethernet or NetworkInterfaceType.Ethernet3Megabit
            or NetworkInterfaceType.FastEthernetFx or NetworkInterfaceType.FastEthernetT
            or NetworkInterfaceType.GigabitEthernet => "Ethernet",
        NetworkInterfaceType.Tunnel => "VPN / tunnel",
        NetworkInterfaceType.Loopback => "loopback",
        NetworkInterfaceType.Ppp => "PPP",
        null => "unknown",
        _ => network.NetworkInterfaceType.ToString()
    };

    // Lets SuspendConnectionAsync/RequestReconnect cancel whichever Connect() attempt is currently in
    // flight, wherever it is in the connect/authenticate/drain sequence, without tearing down the
    // whole reconnect loop.
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
        KeyBundle? Keys,
        CancellationToken Cancel);
}

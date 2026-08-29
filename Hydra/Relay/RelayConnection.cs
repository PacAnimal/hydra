using Cathedral.Utils;
using Common;
using Common.DTO;
using Common.Interfaces;
using Hydra.Config;
using Microsoft.AspNetCore.Http.Connections.Client;
using Microsoft.AspNetCore.SignalR.Client;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Diagnostics;
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
    private RelayTransportSnapshot? _transport;
    private long _connectionAttempts;
    private long _messagesSent;
    private long _messagesReceived;
    private long _bytesSent;
    private long _bytesReceived;
    private long _sendQueueDepth;
    private long _maxSendQueueDepth;
    private long _lastSendLatencyMilliseconds;
    private readonly Lock _sendOrderLock = new();
    private MovementBatch? _openMovementBatch;

    // One ordered outbound queue preserves key/control ordering. Bulk producers use SendReliableAsync and
    // wait until their item has actually left the queue, so a file compressor cannot retain thousands of
    // large payloads. Mouse traffic is capped by InputRouter and coalesced again by the read side below.
    // The queue is deliberately unbounded: after bulk traffic gained backpressure, the remaining producers
    // are small control/input messages and dropping an arbitrary oldest item could lose KeyUp/LeaveScreen.
    private readonly Channel<OutboundMessage> _sendQueue =
        Channel.CreateUnbounded<OutboundMessage>(
            new UnboundedChannelOptions { SingleReader = true, AllowSynchronousContinuations = false });

    protected virtual TimeSpan ReconnectDelay => TimeSpan.FromSeconds(Constants.ReconnectDelaySeconds);

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
                BytesReceived = Interlocked.Read(ref _bytesReceived),
                SendQueueDepth = Interlocked.Read(ref _sendQueueDepth),
                MaxSendQueueDepth = Interlocked.Read(ref _maxSendQueueDepth),
                OldestQueuedMilliseconds = GetOldestQueuedMilliseconds(),
                LastSendLatencyMilliseconds = Interlocked.Read(ref _lastSendLatencyMilliseconds)
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
            if (IsMovementPayload(payload))
            {
                if (_openMovementBatch?.TryAppend(targetHosts, payload) == true) return;
                if (MovementBatch.TryCreate(targetHosts, payload, out var movement))
                {
                    _openMovementBatch = movement;
                    TryQueue(new OutboundMessage(targetHosts, payload, null, CancellationToken.None,
                        Stopwatch.GetTimestamp(), movement));
                    return;
                }
            }

            _openMovementBatch = null;
            TryQueue(new OutboundMessage(targetHosts, payload, null, CancellationToken.None,
                Stopwatch.GetTimestamp(), null));
        }
    }

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
        lock (_sendOrderLock)
        {
            _openMovementBatch = null;
            if (!TryQueue(new OutboundMessage(targetHosts, payload, completion, cancel,
                    Stopwatch.GetTimestamp(), null)))
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
            Interlocked.Increment(ref _connectionAttempts);
            try
            {
                await Connect(netConfig, hostName, cancel);
            }
            catch (OperationCanceledException) when (cancel.IsCancellationRequested)
            {
                break;
            }
            catch (OperationCanceledException)
            {
                log.LogWarning("Relay connection lost — retrying in {ReconnectDelay}s", ReconnectDelay.TotalSeconds);
            }
            catch (HttpRequestException ex)
            {
                log.LogWarning("Relay connection failed — retrying in {ReconnectDelay}s: {Message}", ReconnectDelay.TotalSeconds, ex.InnerException?.Message ?? ex.Message);
            }
            catch (Exception ex)
            {
                log.LogError(ex, "Relay connection failed — retrying in {ReconnectDelay}s", ReconnectDelay.TotalSeconds);
            }
            finally
            {
                var wasConnected = _server != null;
                _server = null;
                _encryption = null;
                _transport = null;
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
            }

            if (!cancel.IsCancellationRequested)
                await Task.Delay(WithJitter(ReconnectDelay), cancel).ConfigureAwait(false);
        }
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
                Interlocked.Increment(ref _messagesSent);
                Interlocked.Add(ref _bytesSent, encrypted.LongLength);
                Interlocked.Exchange(ref _lastSendLatencyMilliseconds,
                    (long)Stopwatch.GetElapsedTime(item.EnqueuedTimestamp).TotalMilliseconds);
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
            Interlocked.Read(ref _bytesReceived),
            Interlocked.Read(ref _sendQueueDepth),
            Interlocked.Read(ref _maxSendQueueDepth),
            GetOldestQueuedMilliseconds(),
            Interlocked.Read(ref _lastSendLatencyMilliseconds));
    }

    private bool TryQueue(OutboundMessage item)
    {
        var depth = Interlocked.Increment(ref _sendQueueDepth);
        UpdateMaxQueueDepth(depth);
        if (_sendQueue.Writer.TryWrite(item)) return true;
        Interlocked.Decrement(ref _sendQueueDepth);
        return false;
    }

    private bool TryReadQueued(out OutboundMessage item)
    {
        if (!_sendQueue.Reader.TryRead(out item!)) return false;
        Interlocked.Decrement(ref _sendQueueDepth);
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

    private void UpdateMaxQueueDepth(long depth)
    {
        var current = Interlocked.Read(ref _maxSendQueueDepth);
        while (depth > current)
        {
            var observed = Interlocked.CompareExchange(ref _maxSendQueueDepth, depth, current);
            if (observed == current) return;
            current = observed;
        }
    }

    private long GetOldestQueuedMilliseconds()
    {
        return _sendQueue.Reader.TryPeek(out var oldest)
            ? (long)Stopwatch.GetElapsedTime(oldest.EnqueuedTimestamp).TotalMilliseconds
            : 0;
    }

    internal static bool TryCoalesceMovement(byte[] current, byte[] next, out byte[] combined)
    {
        combined = current;
        if (!MovementBatch.TryCreate([], current, out var movement) || !movement.TryAppend([], next))
            return false;
        combined = movement.Snapshot();
        return true;
    }

    private static bool IsMovementPayload(byte[] payload) => payload.Length > 0
        && payload[0] is (byte)MessageKind.MouseMove or (byte)MessageKind.MouseMoveDelta;

    private sealed class MovementBatch
    {
        private readonly MessageKind _kind;
        private readonly string[] _targets;
        private byte[] _absolutePayload;
        private int _dx;
        private int _dy;

        private MovementBatch(string[] targets, byte[] payload, MessageKind kind, int dx = 0, int dy = 0)
        {
            _targets = targets;
            _absolutePayload = payload;
            _kind = kind;
            _dx = dx;
            _dy = dy;
        }

        internal static bool TryCreate(string[] targets, byte[] payload, out MovementBatch movement)
        {
            movement = null!;
            if (payload.Length == 0) return false;
            if (payload[0] == (byte)MessageKind.MouseMove)
            {
                movement = new MovementBatch(targets, payload, MessageKind.MouseMove);
                return true;
            }
            if (payload[0] != (byte)MessageKind.MouseMoveDelta || !TryDecodeDelta(payload, out var dx, out var dy))
                return false;
            movement = new MovementBatch(targets, payload, MessageKind.MouseMoveDelta, dx, dy);
            return true;
        }

        internal bool TryAppend(string[] targets, byte[] payload)
        {
            if (!_targets.SequenceEqual(targets) || payload.Length == 0 || payload[0] != (byte)_kind) return false;
            if (_kind == MessageKind.MouseMove)
            {
                _absolutePayload = payload;
                return true;
            }
            if (!TryDecodeDelta(payload, out var dx, out var dy)) return false;
            _dx = (int)Math.Clamp((long)_dx + dx, int.MinValue, int.MaxValue);
            _dy = (int)Math.Clamp((long)_dy + dy, int.MinValue, int.MaxValue);
            return true;
        }

        internal byte[] Snapshot() => _kind == MessageKind.MouseMove
            ? _absolutePayload
            : MessageSerializer.Encode(MessageKind.MouseMoveDelta, new MouseMoveDeltaMessage(_dx, _dy));

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

    private sealed class ConnectionCancellationScope : IDisposable
    {
        private readonly RelayConnection _owner;
        private readonly CancellationTokenSource _cancellation;

        internal ConnectionCancellationScope(RelayConnection owner, CancellationTokenSource cancellation)
        {
            _owner = owner;
            _cancellation = cancellation;
            lock (_owner._connectionLock) _owner._connectionCancellation = cancellation;
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
        CancellationToken Cancel,
        long EnqueuedTimestamp,
        MovementBatch? Movement);
}

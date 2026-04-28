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

namespace Hydra.Relay;

public class RelayConnection(IHydraProfile profile, ILogger<RelayConnection> log, IWorldState peerState)
    : SimpleHostedService(log), IStyxClient, IRelaySender
{
    private IStyxServer? _server;
    private RelayEncryption? _encryption;

    // outbound send queue — written synchronously, drained by the Connect loop
    private readonly Channel<(string[] Targets, byte[] Payload)> _sendQueue =
        Channel.CreateUnbounded<(string[], byte[])>(
            new UnboundedChannelOptions { SingleReader = true });

    protected virtual TimeSpan ReconnectDelay => TimeSpan.FromSeconds(Constants.ReconnectDelaySeconds);

    // IRelaySender
    public bool IsConnected => _server != null;
    public event Func<string[], Task>? PeersChanged;
    public event Func<string, MessageKind, ReadOnlyMemory<byte>, Task>? MessageReceived;
    public event Func<Task>? Disconnected;

    public void Send(string[] targetHosts, byte[] payload)
    {
        if (_server == null || _encryption == null) return;
        _sendQueue.Writer.TryWrite((targetHosts, payload));
    }

    // IStyxClient
    public async Task Receive(string sourceHost, byte[] payload)
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
                while (_sendQueue.Reader.TryRead(out _)) { } // discard stale outbound messages
                if (wasConnected)
                {
                    await OnDisconnected();
                    if (Disconnected != null) await Disconnected();
                }
            }

            if (!cancel.IsCancellationRequested)
                await Task.Delay(ReconnectDelay, cancel).ConfigureAwait(false);
        }
    }

    private async Task Connect(NetworkConfig netConfig, string hostName, CancellationToken cancel)
    {
        using var disco = CancellationTokenSource.CreateLinkedTokenSource(cancel);

        await using var con = new HubConnectionBuilder()
            .WithUrl($"{netConfig.StyxServer}/relay", ConfigureHubUrl)
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

        var response = await server.Authenticate(new RelayLogin
        {
            Authorization = netConfig.Authorization,
            HostName = hostName
        });

        if (!response.Authenticated)
        {
            _server = null;
            _encryption = null;
            log.LogError("Relay authentication failed: {Message}", response.Message);
            return;
        }

        log.LogInformation("Authenticated on relay as {HostName}", hostName);
        await OnAuthenticated();

        // drain outbound queue until the connection drops
        (string[] Targets, byte[] Payload)? lookahead = null;
        while (true)
        {
            (string[] Targets, byte[] Payload) item;
            if (lookahead.HasValue)
            {
                item = lookahead.Value;
                lookahead = null;
            }
            else
            {
                if (!await _sendQueue.Reader.WaitToReadAsync(disco.Token)) break;
                if (!_sendQueue.Reader.TryRead(out item)) continue;
            }

            // coalesce mouse moves — skip intermediate positions, only send the latest
            if (item.Payload.Length > 0 && item.Payload[0] == (byte)MessageKind.MouseMove)
            {
                while (_sendQueue.Reader.TryRead(out var next))
                {
                    if (next.Payload.Length > 0 && next.Payload[0] == (byte)MessageKind.MouseMove && next.Targets.SequenceEqual(item.Targets))
                        item = next;
                    else
                    {
                        lookahead = next;
                        break;
                    }
                }
            }

            try
            {
                var encrypted = await _encryption.Encrypt(item.Payload, cancel);
                await _server.Send(item.Targets, encrypted);
            }
            catch (OperationCanceledException) { break; }
            catch (HttpRequestException ex)
            {
                log.LogWarning("Failed to send relay message to [{TargetHosts}]: {Message}", string.Join(", ", item.Targets), ex.InnerException?.Message ?? ex.Message);
            }
            catch (Exception ex)
            {
                log.LogWarning(ex, "Failed to send relay message to [{TargetHosts}]", string.Join(", ", item.Targets));
            }
        }
    }
}

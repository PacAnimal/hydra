using Cathedral.Config;
using Common;
using Common.DTO;
using Common.Interfaces;
using Hydra.Config;
using Microsoft.AspNetCore.Http.Connections.Client;
using Microsoft.AspNetCore.SignalR.Client;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using System.Net.Sockets;
using System.Threading.Channels;
using TypedSignalR.Client;

namespace Hydra.Relay;

public class RelayConnection(IHydraProfile profile, ILogger<RelayConnection> log, IWorldState peerState)
    : BackgroundService, IStyxClient, IRelaySender
{
    private IStyxServer? _server;
    private RelayEncryption? _encryption;

    // outbound send queue — written synchronously, drained by the Connect loop
    private readonly Channel<(string[] Targets, byte[] Payload)> _sendQueue =
        Channel.CreateUnbounded<(string[], byte[])>(
            new UnboundedChannelOptions { SingleReader = true });

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

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
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

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await Connect(netConfig, hostName, stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (OperationCanceledException)
            {
                log.LogWarning("Relay connection lost — retrying in {ReconnectDelay}s", Constants.ReconnectDelaySeconds);
            }
            catch (HttpRequestException ex)
            {
                log.LogWarning("Relay connection failed — retrying in {ReconnectDelay}s: {Message}", Constants.ReconnectDelaySeconds, ex.InnerException?.Message ?? ex.Message);
            }
            catch (Exception ex)
            {
                log.LogError(ex, "Relay connection failed — retrying in {ReconnectDelay}s", Constants.ReconnectDelaySeconds);
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

            if (!stoppingToken.IsCancellationRequested)
                await Task.Delay(TimeSpan.FromSeconds(Constants.ReconnectDelaySeconds), stoppingToken).ConfigureAwait(false);
        }
    }

    private async Task Connect(NetworkConfig netConfig, string hostName, CancellationToken stoppingToken)
    {
        using var disco = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken);

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
        await foreach (var (targets, payload) in _sendQueue.Reader.ReadAllAsync(disco.Token))
        {
            try
            {
                var encrypted = await _encryption.Encrypt(payload, stoppingToken);
                await _server.Send(targets, encrypted);
            }
            catch (OperationCanceledException) { break; }
            catch (HttpRequestException ex)
            {
                log.LogWarning("Failed to send relay message to [{TargetHosts}]: {Message}", string.Join(", ", targets), ex.InnerException?.Message ?? ex.Message);
            }
            catch (Exception ex)
            {
                log.LogWarning(ex, "Failed to send relay message to [{TargetHosts}]", string.Join(", ", targets));
            }
        }
    }
}

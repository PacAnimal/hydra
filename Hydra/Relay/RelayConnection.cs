using Cathedral.Config;
using Cathedral.Utils;
using Common;
using Common.DTO;
using Common.Interfaces;
using Hydra.Config;
using Microsoft.AspNetCore.Http.Connections.Client;
using Microsoft.AspNetCore.SignalR.Client;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using System.Net.Http;
using System.Net.Sockets;
using TypedSignalR.Client;

namespace Hydra.Relay;

public class RelayConnection(HydraConfig config, ILogger<RelayConnection> log, IWorldState peerState)
    : BackgroundService, IStyxClient, IRelaySender
{
    private IStyxServer? _server;
    private RelayEncryption? _encryption;

    // IRelaySender
    public bool IsConnected => _server != null;
    public event Action<string[]>? PeersChanged;
    public event Action<string, MessageKind, string>? MessageReceived;

    public async ValueTask Send(string[] targetHosts, byte[] payload)
    {
        if (_server == null || _encryption == null) return;
        try
        {
            var encrypted = await _encryption.Encrypt(payload);
            await _server.Send(targetHosts, encrypted);
        }
        catch (Exception ex)
        {
            log.LogWarning(ex, "Failed to send relay message to [{TargetHosts}]", string.Join(", ", targetHosts));
        }
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
            var (kind, json) = MessageSerializer.Decode(decrypted);
            switch (kind)
            {
                case MessageKind.MouseMove:
                case MessageKind.SlaveLog:
                    if (log.IsEnabled(LogLevel.Trace))
                        log.LogTrace("Received {Kind} from {SourceHost} ({Bytes} bytes)", kind, sourceHost, payload.Length);
                    break;
                default:
                    log.LogDebug("Received {Kind} from {SourceHost} ({Bytes} bytes)", kind, sourceHost, payload.Length);
                    break;
            }
            await OnReceive(sourceHost, kind, json);
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
    protected virtual Task OnReceive(string sourceHost, MessageKind kind, string json)
    {
        MessageReceived?.Invoke(sourceHost, kind, json);
        return Task.CompletedTask;
    }

    protected virtual Task OnPeers(string[] hostNames)
    {
        PeersChanged?.Invoke(hostNames);
        return Task.CompletedTask;
    }

    protected virtual Task OnKicked(string reason) => Task.CompletedTask;
    // fires after _server and _encryption are set — guaranteed connection-ready signal
    protected virtual Task OnAuthenticated() => Task.CompletedTask;

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
        if (config.NetworkConfig == null) return;

        NetworkConfig netConfig;
        try
        {
            netConfig = NetworkConfig.Parse(config.NetworkConfig);
        }
        catch (Exception ex)
        {
            log.LogError(ex, "Failed to parse NetworkConfig — relay disabled");
            return;
        }

        var hostName = config.ResolvedName;
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
            catch (Exception ex)
            {
                log.LogError(ex, "Relay connection failed — retrying in 15s");
            }
            finally
            {
                _server = null;
                _encryption = null;
            }

            if (!stoppingToken.IsCancellationRequested)
                await Task.Delay(TimeSpan.FromSeconds(15), stoppingToken).ConfigureAwait(false);
        }
    }

    private async Task Connect(NetworkConfig netConfig, string hostName, CancellationToken stoppingToken)
    {
        using var disco = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken);

        await using var con = new HubConnectionBuilder()
            .WithUrl($"{netConfig.StyxServer}/relay", ConfigureHubUrl)
            .AddJsonProtocol(hubOptions =>
            {
                SaneJson.Configure(hubOptions.PayloadSerializerOptions);
                hubOptions.PayloadSerializerOptions.WriteIndented = false;
            })
            .Build();

        con.Closed += async closeEx =>
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

        // wait until the connection is closed
        await Task.Delay(Timeout.InfiniteTimeSpan, disco.Token).ConfigureAwait(false);
    }
}

using Common.DTO;
using Common.Interfaces;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.SignalR.Client;
using Microsoft.Extensions.DependencyInjection;
using TypedSignalR.Client;

namespace Tests.Setup;

/// <summary>
/// Minimal SignalR test client for protocol-level Styx tests — auth failures, unauthenticated calls, etc.
/// Connects without RelayEncryption; payloads are raw bytes. Use HydraTestClient for real relay tests.
/// </summary>
public sealed class TestStyxClient : IStyxClient, IAsyncDisposable
{
    private HubConnection? _hub;
    private IDisposable? _registration;

    private readonly NotificationQueue<string[]> _peers = new();
    private readonly NotificationQueue<byte[]> _received = new();
    private readonly NotificationQueue<Exception?> _closed = new();
    private readonly SemaphoreSlim _receiveSignal = new(0);
    private readonly SemaphoreSlim _kickSignal = new(0);

    public IStyxServer? Server { get; private set; }
    public (string Source, string SourceIp, byte[] Payload)? LastReceived { get; private set; }
    public string? KickReason { get; private set; }

    // connect and authenticate — returns the auth response for the caller to inspect
    public async Task<RelayLoginResponse> Connect(
        WebApplicationFactory<global::Styx.Program> factory,
        string authorization,
        string hostName,
        string path = global::Styx.Constants.RelayPath)
    {
        await ConnectRaw(factory, path);
        return await Server!.Authenticate(new RelayLogin { Authorization = authorization, HostName = hostName });
    }

    // connect without authenticating — for testing unauthenticated behaviour
    public Task ConnectRaw(WebApplicationFactory<global::Styx.Program> factory, string path = global::Styx.Constants.RelayPath) =>
        ConnectRaw(new HubConnectionBuilder().WithUrl($"{factory.Server.BaseAddress.ToString().TrimEnd('/')}{path}",
            options => options.UseTestServer(factory.Server)));

    // the same, against the embedded relay on a real socket, trusting its self-signed cert without the binding
    public Task ConnectRaw(string serverUrl) => ConnectRaw(new HubConnectionBuilder().WithUrl($"{serverUrl}/relay", options =>
    {
        options.HttpMessageHandlerFactory = _ => new HttpClientHandler { ServerCertificateCustomValidationCallback = (_, _, _, _) => true };
        options.WebSocketConfiguration = ws => ws.RemoteCertificateValidationCallback = (_, _, _, _) => true;
    }));

    private async Task ConnectRaw(IHubConnectionBuilder builder)
    {
        _hub = builder.AddMessagePackProtocol().Build();
        _hub.Closed += error =>
        {
            _closed.Push(error);
            return Task.CompletedTask;
        };

        await _hub.StartAsync();
        _registration = _hub.Register<IStyxClient>(this);
        Server = _hub.CreateHubProxy<IStyxServer>();
    }

    public Task<string[]> WaitForPeers(int timeoutMs = 5000) => _peers.Next(timeoutMs, "peers update");

    public async Task<(string Source, string SourceIp, byte[] Payload)> WaitForReceive(int timeoutMs = 5000)
    {
        if (!await _receiveSignal.WaitAsync(timeoutMs))
            throw new TimeoutException("Timed out waiting for message");
        return LastReceived!.Value;
    }

    // every payload received, in arrival order
    public Task<byte[]> NextReceived(int timeoutMs = 5000) => _received.Next(timeoutMs, "a received payload");

    // a non-blocking invocation, as Hydra's input lane sends: nothing comes back, and the next may go at once
    public Task SendWithoutWaiting(string[] targetHosts, byte[] payload) => _hub!.SendAsync(nameof(IStyxServer.Send), targetHosts, payload);

    // the relay dropped the connection
    public Task<Exception?> WaitForClose(int timeoutMs = 5000) => _closed.Next(timeoutMs, "the connection to close");

    public HubConnectionState State => _hub?.State ?? HubConnectionState.Disconnected;

    public async Task<string> WaitForKick(int timeoutMs = 5000)
    {
        if (!await _kickSignal.WaitAsync(timeoutMs))
            throw new TimeoutException("Timed out waiting for kick");
        return KickReason!;
    }

    // IStyxClient
    public Task Receive(string sourceHost, string sourceIp, byte[] payload)
    {
        LastReceived = (sourceHost, sourceIp, payload);
        _received.Push(payload);
        _receiveSignal.Release();
        return Task.CompletedTask;
    }

    public Task Kicked(string reason)
    {
        KickReason = reason;
        _kickSignal.Release();
        return Task.CompletedTask;
    }

    public Task Peers(string[] hostNames)
    {
        _peers.Push(hostNames);
        return Task.CompletedTask;
    }

    public async ValueTask DisposeAsync()
    {
        _registration?.Dispose();
        if (_hub != null)
        {
            await _hub.StopAsync();
            await _hub.DisposeAsync();
        }
    }
}

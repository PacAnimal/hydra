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

    private readonly SemaphoreSlim _peerSignal = new(0);
    private readonly SemaphoreSlim _receiveSignal = new(0);
    private readonly SemaphoreSlim _kickSignal = new(0);

    public IStyxServer? Server { get; private set; }
    public string[] LastPeers { get; private set; } = [];
    public (string Source, byte[] Payload)? LastReceived { get; private set; }
    public string? KickReason { get; private set; }

    // connect and authenticate — returns the auth response for the caller to inspect
    public async Task<RelayLoginResponse> Connect(
        WebApplicationFactory<global::Styx.Program> factory,
        string authorization,
        string hostName)
    {
        await ConnectRaw(factory);
        return await Server!.Authenticate(new RelayLogin { Authorization = authorization, HostName = hostName });
    }

    // connect without authenticating — for testing unauthenticated behaviour
    public async Task ConnectRaw(WebApplicationFactory<global::Styx.Program> factory)
    {
        _hub = new HubConnectionBuilder()
            .WithUrl($"{factory.Server.BaseAddress}relay",
                options => options.HttpMessageHandlerFactory = _ => factory.Server.CreateHandler())
            .AddMessagePackProtocol()
            .Build();

        await _hub.StartAsync();
        _registration = _hub.Register<IStyxClient>(this);
        Server = _hub.CreateHubProxy<IStyxServer>();
    }

    public async Task<string[]> WaitForPeers(int timeoutMs = 5000)
    {
        if (!await _peerSignal.WaitAsync(timeoutMs))
            throw new TimeoutException("Timed out waiting for peers update");
        return LastPeers;
    }

    public async Task<(string Source, byte[] Payload)> WaitForReceive(int timeoutMs = 5000)
    {
        if (!await _receiveSignal.WaitAsync(timeoutMs))
            throw new TimeoutException("Timed out waiting for message");
        return LastReceived!.Value;
    }

    public async Task<string> WaitForKick(int timeoutMs = 5000)
    {
        if (!await _kickSignal.WaitAsync(timeoutMs))
            throw new TimeoutException("Timed out waiting for kick");
        return KickReason!;
    }

    // IStyxClient
    public Task Receive(string sourceHost, byte[] payload)
    {
        LastReceived = (sourceHost, payload);
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
        LastPeers = hostNames;
        _peerSignal.Release();
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

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
public sealed class HydraTestClient(WebApplicationFactory<global::Styx.Program> factory, IHydraProfile profile) : RelayConnection(profile, TestLog.CreateLogger<RelayConnection>(), new WorldState()), IAsyncDisposable
{
    private readonly WebApplicationFactory<global::Styx.Program> _factory = factory;

    private readonly SemaphoreSlim _readySignal = new(0);
    private readonly SemaphoreSlim _peerSignal = new(0);
    private readonly SemaphoreSlim _receiveSignal = new(0);
    private readonly SemaphoreSlim _kickSignal = new(0);

    private volatile string[] _lastPeers = [];
    private (string Source, MessageKind Kind, string Json)? _lastMessage;
    private string? _kickReason;

    public string[] LastPeers => _lastPeers;
    public (string Source, MessageKind Kind, string Json)? LastMessage => _lastMessage;
    public string? KickReason => _kickReason;

    protected override Task OnAuthenticated() { _readySignal.Release(); return Task.CompletedTask; }

    // route the hub connection through the in-memory test server handler
    protected override void ConfigureHubUrl(HttpConnectionOptions options)
    {
        options.HttpMessageHandlerFactory = _ => _factory.Server.CreateHandler();
    }

    protected override Task OnReceive(string sourceHost, MessageKind kind, string json)
    {
        _lastMessage = (sourceHost, kind, json);
        _receiveSignal.Release();
        return Task.CompletedTask;
    }

    protected override Task OnPeers(string[] hostNames)
    {
        _lastPeers = hostNames;
        _peerSignal.Release();
        return Task.CompletedTask;
    }

    protected override Task OnKicked(string reason)
    {
        _kickReason = reason;
        _kickSignal.Release();
        return Task.CompletedTask;
    }

    // blocks until _server and _encryption are set — use this as the connection-ready barrier
    public async Task WaitForReady(int timeoutMs = 5000)
    {
        if (!await _readySignal.WaitAsync(timeoutMs))
            throw new TimeoutException("Timed out waiting for relay connection");
    }

    // blocks until the next Peers broadcast arrives
    public async Task<string[]> WaitForPeers(int timeoutMs = 5000)
    {
        if (!await _peerSignal.WaitAsync(timeoutMs))
            throw new TimeoutException("Timed out waiting for peers update");
        return _lastPeers;
    }

    public async Task<(string Source, MessageKind Kind, string Json)> WaitForMessage(int timeoutMs = 5000)
    {
        if (!await _receiveSignal.WaitAsync(timeoutMs))
            throw new TimeoutException("Timed out waiting for message");
        return _lastMessage!.Value;
    }

    public async Task<string> WaitForKick(int timeoutMs = 5000)
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

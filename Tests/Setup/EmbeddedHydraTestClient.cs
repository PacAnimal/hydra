using System.Text;
using Hydra.Config;
using Hydra.Relay;

namespace Tests.Setup;

/// <summary>
/// Like HydraTestClient but connects to a real TCP URL — used for embedded Styx server tests.
/// Does not override ConfigureHubUrl, so the base implementation (NoDelay socket) is used.
/// </summary>
public sealed class EmbeddedHydraTestClient(IHydraProfile profile)
    : RelayConnection(profile, TestLog.CreateLogger<RelayConnection>(), new WorldState()), IAsyncDisposable
{
    private readonly SemaphoreSlim _readySignal = new(0);
    private readonly SemaphoreSlim _peerSignal = new(0);
    private readonly SemaphoreSlim _receiveSignal = new(0);
    private readonly SemaphoreSlim _kickSignal = new(0);

    private volatile string[] _lastPeers = [];
    private (string Source, MessageKind Kind, string Json)? _lastMessage;
    private string? _kickReason;

    protected override Task OnAuthenticated() { _readySignal.Release(); return Task.CompletedTask; }

    protected override Task OnReceive(string sourceHost, MessageKind kind, ReadOnlyMemory<byte> body)
    {
        _lastMessage = (sourceHost, kind, Encoding.UTF8.GetString(body.Span));
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

    public async Task WaitForReady(int timeoutMs = 5000)
    {
        if (!await _readySignal.WaitAsync(timeoutMs))
            throw new TimeoutException("Timed out waiting for relay connection");
    }

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

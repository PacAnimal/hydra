using Microsoft.Extensions.Hosting;
using System.Text.Json;

namespace Hydra.Relay;

public sealed record PeerLatencySnapshot(
    string Host,
    double LastRttMs,
    double AverageRttMs,
    double P95RttMs,
    double JitterMs,
    long Samples,
    long Lost,
    DateTimeOffset UpdatedAt);

internal sealed class RelayLatencyService(
    IRelaySender relay,
    Func<long>? getTickCount = null) : BackgroundService
{
    private static readonly TimeSpan ProbeInterval = TimeSpan.FromSeconds(1);
    private static readonly TimeSpan ProbeTimeout = TimeSpan.FromSeconds(5);
    private const int WindowSize = 60;

    private readonly Func<long> _getTickCount = getTickCount ?? (() => Environment.TickCount64);
    private readonly Lock _lock = new();
    private readonly HashSet<string> _peers = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<(string Host, long Sequence), long> _pending = new();
    private readonly Dictionary<string, PeerState> _states = new(StringComparer.OrdinalIgnoreCase);
    private long _sequence;

    public override Task StartAsync(CancellationToken cancellationToken)
    {
        relay.PeersChanged += OnPeersChanged;
        relay.MessageReceived += OnMessageReceived;
        relay.Disconnected += OnDisconnected;
        return base.StartAsync(cancellationToken);
    }

    public override async Task StopAsync(CancellationToken cancellationToken)
    {
        relay.PeersChanged -= OnPeersChanged;
        relay.MessageReceived -= OnMessageReceived;
        relay.Disconnected -= OnDisconnected;
        await base.StopAsync(cancellationToken);
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(ProbeInterval);
        while (await timer.WaitForNextTickAsync(stoppingToken))
            SendProbes();
    }

    internal IReadOnlyList<PeerLatencySnapshot> GetSnapshot()
    {
        lock (_lock)
            return [.. _states
                .Select(pair => pair.Value.Snapshot(pair.Key))
                .OrderBy(item => item.Host, StringComparer.OrdinalIgnoreCase)];
    }

    internal void SendProbes()
    {
        var now = _getTickCount();
        List<(string Host, long Sequence)> sends;
        lock (_lock)
        {
            ExpirePending(now);
            sends = [.. _peers.Select(host => (host, Interlocked.Increment(ref _sequence)))];
            foreach (var (host, sequence) in sends)
                _pending[(host, sequence)] = now;
        }

        foreach (var (host, sequence) in sends)
            relay.Send([host], MessageSerializer.Encode(MessageKind.LatencyProbe, new LatencyProbeMessage(sequence)));
    }

    private Task OnPeersChanged(string[] hosts)
    {
        lock (_lock)
        {
            _peers.Clear();
            foreach (var host in hosts) _peers.Add(host);

            foreach (var key in _pending.Keys.Where(key => !_peers.Contains(key.Host)).ToArray())
                _pending.Remove(key);
            foreach (var host in _states.Keys.Where(host => !_peers.Contains(host)).ToArray())
                _states.Remove(host);
        }
        SendProbes();
        return Task.CompletedTask;
    }

    private Task OnDisconnected()
    {
        lock (_lock)
        {
            _peers.Clear();
            _pending.Clear();
        }
        return Task.CompletedTask;
    }

    private Task OnMessageReceived(string sourceHost, MessageKind kind, ReadOnlyMemory<byte> body)
    {
        try
        {
            switch (kind)
            {
                case MessageKind.LatencyProbe:
                    {
                        var request = JsonSerializer.Deserialize<LatencyProbeMessage>(body.Span, Cathedral.Config.SaneJson.Options);
                        if (request != null)
                            relay.Send([sourceHost], MessageSerializer.Encode(MessageKind.LatencyProbeResponse,
                                new LatencyProbeResponseMessage(request.Sequence)));
                        break;
                    }
                case MessageKind.LatencyProbeResponse:
                    {
                        var response = JsonSerializer.Deserialize<LatencyProbeResponseMessage>(body.Span, Cathedral.Config.SaneJson.Options);
                        if (response != null) RecordResponse(sourceHost, response.Sequence);
                        break;
                    }
            }
        }
        catch (JsonException) { }
        return Task.CompletedTask;
    }

    private void RecordResponse(string host, long sequence)
    {
        var now = _getTickCount();
        lock (_lock)
        {
            if (!_pending.Remove((host, sequence), out var sentAt)) return;
            var rtt = Math.Max(0, now - sentAt);
            if (!_states.TryGetValue(host, out var state))
                _states[host] = state = new PeerState();
            state.Add(rtt);
        }
    }

    private void ExpirePending(long now)
    {
        var cutoff = now - (long)ProbeTimeout.TotalMilliseconds;
        foreach (var pending in _pending.Where(pair => pair.Value <= cutoff).Select(pair => pair.Key).ToArray())
        {
            _pending.Remove(pending);
            if (!_states.TryGetValue(pending.Host, out var state))
                _states[pending.Host] = state = new PeerState();
            state.Lost++;
        }
    }

    private sealed class PeerState
    {
        internal readonly Queue<double> Samples = new();
        internal long TotalSamples;
        internal long Lost;
        internal double Jitter;
        internal double? PreviousRtt;
        internal DateTimeOffset UpdatedAt;

        internal void Add(double rtt)
        {
            if (Samples.Count == WindowSize) Samples.Dequeue();
            Samples.Enqueue(rtt);
            TotalSamples++;
            if (PreviousRtt.HasValue)
                Jitter += (Math.Abs(rtt - PreviousRtt.Value) - Jitter) / 16d;
            PreviousRtt = rtt;
            UpdatedAt = DateTimeOffset.UtcNow;
        }

        internal PeerLatencySnapshot Snapshot(string host)
        {
            if (Samples.Count == 0)
                return new PeerLatencySnapshot(host, 0, 0, 0, Jitter, TotalSamples, Lost, UpdatedAt);
            var ordered = Samples.Order().ToArray();
            var p95Index = Math.Clamp((int)Math.Ceiling(ordered.Length * 0.95) - 1, 0, ordered.Length - 1);
            return new PeerLatencySnapshot(
                host,
                Samples.Last(),
                Samples.Average(),
                ordered[p95Index],
                Jitter,
                TotalSamples,
                Lost,
                UpdatedAt);
        }
    }
}

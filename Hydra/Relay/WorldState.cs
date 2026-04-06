using Cathedral.Extensions;
using Cathedral.Utils;
using Microsoft.Extensions.Logging;
using System.Collections.Concurrent;

namespace Hydra.Relay;

public interface IWorldState
{
    // -- master-side --
    ValueTask<PeerDelta> UpdatePeers(HashSet<string> currentPeers, HashSet<string> configuredSlaves);
    ValueTask SetPeerScreens(string host, List<ScreenInfoEntry> screens);
    ValueTask<Dictionary<string, List<ScreenInfoEntry>>> GetPeerScreensSnapshot();
    ILogger GetOrCreateSlaveLogger(string category, ILoggerFactory factory);

    // -- slave-side --
    ValueTask AddMaster(string host);
    ValueTask<string[]> GetMasters();
    ValueTask PruneMasters(HashSet<string> activePeers);

    // -- shared (encryption) --
    ValueTask SetRemoteKey(string host, SimpleAesKey key);
    ValueTask<SimpleAesKey?> GetRemoteKey(string host);

    // -- master-side (relay reconnect) --
    ValueTask ClearPeers();
}

public class WorldState : IWorldState
{
    private readonly SemaphoreSlimValue<MasterState> _master = new(new MasterState(), disposeValue: false);
    private readonly SemaphoreSlimValue<SlaveState> _slave = new(new SlaveState(), disposeValue: false);
    private readonly SemaphoreSlimValue<SharedState> _shared = new(new SharedState(), disposeValue: false);
    private readonly ConcurrentDictionary<string, ILogger> _loggers = new(StringComparer.OrdinalIgnoreCase);

    public async ValueTask<PeerDelta> UpdatePeers(HashSet<string> currentPeers, HashSet<string> configuredSlaves)
    {
        List<string> newPeers;
        List<string> departed;
        Dictionary<string, List<ScreenInfoEntry>> snapshot;

        using (var m = await _master.WaitForDisposable())
        {
            var s = m.Value;
            newPeers = [.. currentPeers.Where(h => !s.KnownPeers.Contains(h) && configuredSlaves.Contains(h))];
            departed = [.. s.KnownPeers.Where(h => !currentPeers.Contains(h))];

            foreach (var host in departed)
            {
                s.KnownPeers.Remove(host);
                s.PeerScreens.Remove(host);
                foreach (var k in _loggers.Keys.Where(k => k.StartsWithIgnoreCase($"slave:{host}/")).ToList())
                    _loggers.TryRemove(k, out _);
            }
            s.KnownPeers.UnionWith(currentPeers);
            snapshot = s.PeerScreens.ToDictionary(kv => kv.Key, kv => kv.Value, StringComparer.OrdinalIgnoreCase);
        }

        // prune encryption keys and slave masters for departed hosts
        if (departed.Count > 0)
        {
            using (var sh = await _shared.WaitForDisposable())
                foreach (var host in departed)
                    sh.Value.RemoteKeys.Remove(host);

            using var sl = await _slave.WaitForDisposable();
            sl.Value.Masters.RemoveWhere(departed.Contains);
        }

        return new PeerDelta(newPeers, departed.Count > 0, snapshot);
    }

    public async ValueTask SetPeerScreens(string host, List<ScreenInfoEntry> screens)
    {
        using var m = await _master.WaitForDisposable();
        m.Value.PeerScreens[host] = screens;
    }

    public async ValueTask<Dictionary<string, List<ScreenInfoEntry>>> GetPeerScreensSnapshot()
    {
        using var m = await _master.WaitForDisposable();
        return m.Value.PeerScreens.ToDictionary(kv => kv.Key, kv => kv.Value, StringComparer.OrdinalIgnoreCase);
    }

    public ILogger GetOrCreateSlaveLogger(string category, ILoggerFactory factory) =>
        _loggers.GetOrAdd(category, c => factory.CreateLogger(c));

    public async ValueTask AddMaster(string host)
    {
        using var s = await _slave.WaitForDisposable();
        s.Value.Masters.Add(host);
    }

    public async ValueTask<string[]> GetMasters()
    {
        using var s = await _slave.WaitForDisposable();
        return [.. s.Value.Masters];
    }

    public async ValueTask PruneMasters(HashSet<string> activePeers)
    {
        using var s = await _slave.WaitForDisposable();
        s.Value.Masters.RemoveWhere(h => !activePeers.Contains(h));
    }

    public async ValueTask ClearPeers()
    {
        using var m = await _master.WaitForDisposable();
        var s = m.Value;
        s.KnownPeers.Clear();
        s.PeerScreens.Clear();
        _loggers.Clear();
    }

    public async ValueTask SetRemoteKey(string host, SimpleAesKey key)
    {
        using var s = await _shared.WaitForDisposable();
        s.Value.RemoteKeys[host] = key;
    }

    public async ValueTask<SimpleAesKey?> GetRemoteKey(string host)
    {
        using var s = await _shared.WaitForDisposable();
        return s.Value.RemoteKeys.TryGetValue(host, out var key) ? key : null;
    }

    private class MasterState
    {
        public HashSet<string> KnownPeers = new(StringComparer.OrdinalIgnoreCase);
        public Dictionary<string, List<ScreenInfoEntry>> PeerScreens = new(StringComparer.OrdinalIgnoreCase);
    }

    private class SlaveState
    {
        public HashSet<string> Masters = new(StringComparer.OrdinalIgnoreCase);
    }

    private class SharedState
    {
        public Dictionary<string, SimpleAesKey> RemoteKeys = new(StringComparer.OrdinalIgnoreCase);
    }
}

public record PeerDelta(
    List<string> NewPeers,
    bool AnyDeparted,
    Dictionary<string, List<ScreenInfoEntry>> PeerScreensSnapshot);

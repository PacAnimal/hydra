using Cathedral.Extensions;

namespace Hydra.Management;

internal sealed class RemoteManagementStore
{
    internal static readonly TimeSpan PairingLifetime = TimeSpan.FromMinutes(10);
    private readonly string _path;
    private readonly string _lockPath;
    private readonly SemaphoreSlim _lock = new(1, 1);

    public RemoteManagementStore(HydraRuntimeInfo runtime) : this(runtime.ConfigPath) { }

    internal RemoteManagementStore(string configPath)
    {
        var dir = new ConfigDir(configPath);
        _path = dir.ManagementState;
        _lockPath = dir.ManagementLock;
    }

    internal async Task<string> CreatePairingCode(CancellationToken cancel = default)
    {
        var code = RemoteManagementCrypto.RandomSecret();
        await Mutate(state =>
        {
            state.PairingCodes.RemoveAll(item => item.ExpiresAt <= DateTimeOffset.UtcNow);
            state.PairingCodes.Add(new StoredPairingCode(RemoteManagementCrypto.HashPairingCode(code), DateTimeOffset.UtcNow + PairingLifetime));
        }, cancel);
        return code;
    }

    internal async Task<bool> ConsumePairingCode(string code, CancellationToken cancel)
    {
        var consumed = false;
        await Mutate(state =>
        {
            var hash = RemoteManagementCrypto.HashPairingCode(code);
            var match = state.PairingCodes.FirstOrDefault(item => item.ExpiresAt > DateTimeOffset.UtcNow
                && RemoteManagementCrypto.HashesEqual(item.Hash, hash));
            if (match == null) return;
            consumed = true;
            state.PairingCodes.Remove(match);
            state.PairingCodes.RemoveAll(item => item.ExpiresAt <= DateTimeOffset.UtcNow);
        }, cancel);
        return consumed;
    }

    internal async Task<TargetCredential> CreateTargetCredential(CancellationToken cancel)
    {
        var controllerId = "";
        await Mutate(state => controllerId = state.ControllerId, cancel);
        return new TargetCredential(controllerId, RemoteManagementCrypto.RandomSecret());
    }

    internal Task SaveTarget(string host, string secret, CancellationToken cancel) => Mutate(state =>
    {
        state.Targets.RemoveAll(item => item.Host.EqualsIgnoreCase(host));
        state.Targets.Add(new StoredRemoteTarget(host, secret));
    }, cancel);

    internal async Task<TargetCredential?> GetTarget(string host, CancellationToken cancel)
    {
        var state = await Read(cancel);
        var target = state.Targets.FirstOrDefault(item => item.Host.EqualsIgnoreCase(host));
        return target == null ? null : new TargetCredential(state.ControllerId, target.Secret);
    }

    internal Task SaveController(string id, string secret, CancellationToken cancel) => Mutate(state =>
    {
        state.Controllers.RemoveAll(item => item.Id.Equals(id, StringComparison.Ordinal));
        state.Controllers.Add(new StoredRemoteController(id, secret));
    }, cancel);

    internal async Task<string?> GetControllerSecret(string id, CancellationToken cancel)
    {
        var state = await Read(cancel);
        return state.Controllers.FirstOrDefault(item => item.Id.Equals(id, StringComparison.Ordinal))?.Secret;
    }

    internal async Task<bool> RememberRequest(string controllerId, string nonce, long seenAtUnixMs, CancellationToken cancel)
    {
        var accepted = false;
        await Mutate(state =>
        {
            var cutoff = DateTimeOffset.UtcNow.Subtract(RemoteManagementProtocol.ClockSkew).ToUnixTimeMilliseconds();
            state.ReplayNonces.RemoveAll(item => item.SeenAtUnixMs < cutoff);
            if (state.ReplayNonces.Any(item => item.ControllerId.Equals(controllerId, StringComparison.Ordinal)
                && item.Nonce.Equals(nonce, StringComparison.Ordinal))) return;
            while (state.ReplayNonces.Count >= 2048) state.ReplayNonces.RemoveAt(0);
            state.ReplayNonces.Add(new StoredReplayNonce(controllerId, nonce, seenAtUnixMs));
            accepted = true;
        }, cancel);
        return accepted;
    }

    // rewrites an existing sidecar so its private DACL names whoever is at the console now, not at its last write
    internal async Task Restamp(CancellationToken cancel)
    {
        await using var held = await Locked(cancel);
        if (File.Exists(_path)) await WriteUnlocked(await ReadUnlocked(cancel), cancel);
    }

    // reads take the cross-process lock too (see ConfigFileLock)
    private async Task<RemoteManagementState> Read(CancellationToken cancel)
    {
        await using var held = await Locked(cancel);
        return await ReadUnlocked(cancel);
    }

    private async Task Mutate(Action<RemoteManagementState> mutation, CancellationToken cancel)
    {
        await using var held = await Locked(cancel);
        var state = await ReadUnlocked(cancel);
        mutation(state);
        await WriteUnlocked(state, cancel);
    }

    // the in-process lock and then the cross-process one, released together
    private async Task<IAsyncDisposable> Locked(CancellationToken cancel)
    {
        await _lock.WaitAsync(cancel);
        try
        {
            return new Held(_lock, await ConfigFileLock.Acquire(_lockPath, cancel));
        }
        catch
        {
            _lock.Release();
            throw;
        }
    }

    private sealed class Held(SemaphoreSlim gate, IAsyncDisposable fileLock) : IAsyncDisposable
    {
        public async ValueTask DisposeAsync()
        {
            try { await fileLock.DisposeAsync(); }
            finally { gate.Release(); }
        }
    }

    private async Task<RemoteManagementState> ReadUnlocked(CancellationToken cancel)
    {
        if (!File.Exists(_path)) return Empty();
        if (new FileInfo(_path).LinkTarget != null)
            throw new IOException("Hydra remote-management state cannot be a symbolic link.");
        var json = await File.ReadAllTextAsync(_path, cancel);
        var persisted = ManagementJson.Deserialize<PersistedState>(json);
        return new RemoteManagementState(
            string.IsNullOrWhiteSpace(persisted.ControllerId) ? RemoteManagementCrypto.RandomSecret(18) : persisted.ControllerId,
            persisted.Targets ?? [],
            persisted.Controllers ?? [],
            persisted.PairingCodes ?? [],
            persisted.ReplayNonces ?? []);
    }

    // A field added after this file format shipped, or a hand-edited sidecar, deserializes as null
    // rather than throwing — unlike RemoteManagementState, which every other caller can rely on being
    // fully populated once ReadUnlocked has normalized it.
    private sealed record PersistedState(
        string? ControllerId,
        List<StoredRemoteTarget>? Targets,
        List<StoredRemoteController>? Controllers,
        List<StoredPairingCode>? PairingCodes,
        List<StoredReplayNonce>? ReplayNonces);

    private async Task WriteUnlocked(RemoteManagementState state, CancellationToken cancel) =>
        await PrivateFile.Write(_path, ManagementJson.Serialize(state), PrivateFile.OwnerOnly, cancel);

    private static RemoteManagementState Empty() => new(RemoteManagementCrypto.RandomSecret(18), [], [], [], []);
}

internal sealed record TargetCredential(string ControllerId, string Secret);

using System.Text;

namespace Hydra.Management;

internal sealed class RemoteManagementStore
{
    private static readonly TimeSpan PairingLifetime = TimeSpan.FromMinutes(10);
    private static readonly TimeSpan LockTimeout = TimeSpan.FromSeconds(5);
    private readonly string _path;
    private readonly string _lockPath;
    private readonly SemaphoreSlim _lock = new(1, 1);

    public RemoteManagementStore(HydraRuntimeInfo runtime) : this(runtime.ConfigPath) { }

    internal RemoteManagementStore(string configPath)
    {
        var directory = Path.GetDirectoryName(Path.GetFullPath(configPath))!;
        _path = Path.Combine(directory, ".hydra-management.json");
        _lockPath = Path.Combine(directory, ".hydra-management.lock");
    }

    internal async Task<string> CreatePairingCodeAsync(CancellationToken cancel = default)
    {
        var code = RemoteManagementCrypto.RandomSecret();
        await MutateAsync(state =>
        {
            state.PairingCodes.RemoveAll(item => item.ExpiresAt <= DateTimeOffset.UtcNow);
            state.PairingCodes.Add(new StoredPairingCode(RemoteManagementCrypto.HashPairingCode(code), DateTimeOffset.UtcNow + PairingLifetime));
        }, cancel);
        return code;
    }

    internal async Task<bool> ConsumePairingCodeAsync(string code, CancellationToken cancel)
    {
        var consumed = false;
        await MutateAsync(state =>
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

    internal async Task<TargetCredential> CreateTargetCredentialAsync(CancellationToken cancel)
    {
        var controllerId = "";
        await MutateAsync(state => controllerId = state.ControllerId, cancel);
        return new TargetCredential(controllerId, RemoteManagementCrypto.RandomSecret());
    }

    internal Task SaveTargetAsync(string host, string secret, CancellationToken cancel) => MutateAsync(state =>
    {
        state.Targets.RemoveAll(item => item.Host.Equals(host, StringComparison.OrdinalIgnoreCase));
        state.Targets.Add(new StoredRemoteTarget(host, secret));
    }, cancel);

    internal async Task<TargetCredential?> GetTargetAsync(string host, CancellationToken cancel)
    {
        var state = await ReadAsync(cancel);
        var target = state.Targets.FirstOrDefault(item => item.Host.Equals(host, StringComparison.OrdinalIgnoreCase));
        return target == null ? null : new TargetCredential(state.ControllerId, target.Secret);
    }

    internal Task SaveControllerAsync(string id, string secret, CancellationToken cancel) => MutateAsync(state =>
    {
        state.Controllers.RemoveAll(item => item.Id.Equals(id, StringComparison.Ordinal));
        state.Controllers.Add(new StoredRemoteController(id, secret));
    }, cancel);

    internal async Task<string?> GetControllerSecretAsync(string id, CancellationToken cancel)
    {
        var state = await ReadAsync(cancel);
        return state.Controllers.FirstOrDefault(item => item.Id.Equals(id, StringComparison.Ordinal))?.Secret;
    }

    internal async Task<bool> RememberRequestAsync(string controllerId, string nonce, long seenAtUnixMs, CancellationToken cancel)
    {
        var accepted = false;
        await MutateAsync(state =>
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

    private async Task<RemoteManagementState> ReadAsync(CancellationToken cancel)
    {
        await _lock.WaitAsync(cancel);
        try { return await ReadUnlockedAsync(cancel); }
        finally { _lock.Release(); }
    }

    private async Task MutateAsync(Action<RemoteManagementState> mutation, CancellationToken cancel)
    {
        await _lock.WaitAsync(cancel);
        try
        {
            await using var fileLock = await AcquireMutationLockAsync(cancel);
            var state = await ReadUnlockedAsync(cancel);
            mutation(state);
            await WriteUnlockedAsync(state, cancel);
        }
        finally { _lock.Release(); }
    }

    private async Task<RemoteManagementState> ReadUnlockedAsync(CancellationToken cancel)
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
    // fully populated once ReadUnlockedAsync has normalized it.
    private sealed record PersistedState(
        string? ControllerId,
        List<StoredRemoteTarget>? Targets,
        List<StoredRemoteController>? Controllers,
        List<StoredPairingCode>? PairingCodes,
        List<StoredReplayNonce>? ReplayNonces);

    private async Task<FileStream> AcquireMutationLockAsync(CancellationToken cancel)
    {
        var directory = Path.GetDirectoryName(_lockPath)!;
        Directory.CreateDirectory(directory);
        var deadline = DateTimeOffset.UtcNow + LockTimeout;
        while (true)
        {
            cancel.ThrowIfCancellationRequested();
            if (File.Exists(_lockPath) && new FileInfo(_lockPath).LinkTarget != null)
                throw new IOException("Hydra remote-management lock cannot be a symbolic link.");
            try
            {
                var stream = new FileStream(_lockPath, FileMode.OpenOrCreate, FileAccess.ReadWrite,
                    FileShare.None, 1, FileOptions.Asynchronous);
                if (!OperatingSystem.IsWindows())
                    File.SetUnixFileMode(_lockPath, UnixFileMode.UserRead | UnixFileMode.UserWrite);
                return stream;
            }
            catch (IOException) when (DateTimeOffset.UtcNow < deadline)
            {
                await Task.Delay(TimeSpan.FromMilliseconds(25), cancel);
            }
        }
    }

    private async Task WriteUnlockedAsync(RemoteManagementState state, CancellationToken cancel)
    {
        var directory = Path.GetDirectoryName(_path)!;
        Directory.CreateDirectory(directory);
        var temp = Path.Combine(directory, $".{Path.GetFileName(_path)}.{Guid.NewGuid():N}.tmp");
        try
        {
            var bytes = new UTF8Encoding(false).GetBytes(ManagementJson.Serialize(state));
            await using (var stream = new FileStream(temp, FileMode.CreateNew, FileAccess.Write, FileShare.None,
                             4096, FileOptions.Asynchronous | FileOptions.WriteThrough))
            {
                await stream.WriteAsync(bytes, cancel);
                await stream.FlushAsync(cancel);
                stream.Flush(flushToDisk: true);
            }
            if (!OperatingSystem.IsWindows())
                File.SetUnixFileMode(temp, UnixFileMode.UserRead | UnixFileMode.UserWrite);
            File.Move(temp, _path, true);
        }
        finally
        {
            if (File.Exists(temp)) File.Delete(temp);
        }
    }

    private static RemoteManagementState Empty() => new(RemoteManagementCrypto.RandomSecret(18), [], [], [], []);
}

internal sealed record TargetCredential(string ControllerId, string Secret);

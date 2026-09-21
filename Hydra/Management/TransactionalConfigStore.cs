using System.Security.Cryptography;
using System.Text;
using Hydra.Config;

namespace Hydra.Management;

internal sealed class TransactionalConfigStore(HydraRuntimeInfo runtime)
{
    private readonly SemaphoreSlim _writeLock = new(1, 1);

    /// <summary>
    /// Reads under the cross-process config lock — the TUI is a SEPARATE PROCESS from the service and both
    /// read this file, so an unlocked read here is a read racing another program's save. On Windows it does
    /// not merely read a stale copy: the reader's handle refuses the saver's replace and the SAVE fails, so
    /// a user's config edit is lost because something else happened to be reading.
    /// </summary>
    internal async Task<ConfigDocument> ReadAsync(CancellationToken cancel = default)
    {
        await using var fileLock = await ConfigFileLock.Acquire(ConfigFileLock.ConfigPathFor(runtime.ConfigPath), cancel);
        return await ReadUnlockedAsync(cancel);
    }

    /// <summary>
    /// The same read for a caller that ALREADY HOLDS the config lock. The lock is exclusive and not
    /// re-entrant — taking it twice in one call stack does not nest, it waits for itself until the budget
    /// runs out and then throws — so a path that holds it must come through here. Rollback is that path.
    /// </summary>
    internal async Task<ConfigDocument> ReadUnlockedAsync(CancellationToken cancel = default)
    {
        var json = await File.ReadAllTextAsync(runtime.ConfigPath, cancel);
        return new ConfigDocument(runtime.ConfigPath, Revision(json), json);
    }

    internal static ConfigValidation Validate(string json)
    {
        try
        {
            _ = HydraConfigFile.Parse(json, "<tui>");
            return new ConfigValidation(true);
        }
        catch (Exception ex) when (ex is InvalidOperationException or System.Text.Json.JsonException)
        {
            return new ConfigValidation(false, ex.Message);
        }
    }

    internal async Task<ConfigDocument> SaveAsync(string expectedRevision, string json, CancellationToken cancel,
        bool allowPendingRemoteApply = false)
    {
        if (!allowPendingRemoteApply && RemoteApplyStore.HasPendingTransaction(runtime.ConfigPath))
            throw new InvalidOperationException("A remote configuration candidate is awaiting confirmation or rollback.");
        var validation = Validate(json);
        if (!validation.Valid) throw new InvalidOperationException(validation.Error);

        await _writeLock.WaitAsync(cancel);
        try
        {
            // The cross-process lock spans the compare AND the write. Held only for the write, the revision
            // check is a TOCTOU across processes: the TUI and the service can both read, both find the
            // revision they expected, and both save — and the second silently discards the first.
            await using var fileLock = await ConfigFileLock.Acquire(ConfigFileLock.ConfigPathFor(runtime.ConfigPath), cancel);

            var current = await File.ReadAllTextAsync(runtime.ConfigPath, cancel);
            if (!Revision(current).Equals(expectedRevision, StringComparison.Ordinal))
                throw new InvalidOperationException("hydra.conf changed outside the TUI. Reload before saving.");

            var mode = OperatingSystem.IsWindows() ? default : File.GetUnixFileMode(runtime.ConfigPath);
            await PrivateFile.Write(runtime.ConfigPath, json, mode, cancel);

            // Re-parsed from DISK, because the claim worth making is that what LANDED is loadable — the
            // content was already validated at :52, so the only failure left is bytes not surviving the
            // trip. And a failure here PUTS THE OLD CONFIG BACK. The hand-rolled write this replaced parsed
            // its temp before promoting it, so a bad write could never become hydra.conf; parsing after the
            // rename demoted that gate to an alarm, leaving the caller told it failed while the disk said
            // otherwise. For a KVM whose whole remote-apply machinery exists to stop a config change
            // bricking a distant box, the guarantee is worth restoring even at the cost of a second write.
            try
            {
                _ = HydraConfigFile.Parse(await File.ReadAllTextAsync(runtime.ConfigPath, cancel), runtime.ConfigPath);
            }
            catch
            {
                await PrivateFile.Write(runtime.ConfigPath, current, mode, cancel);
                throw;
            }

            return new ConfigDocument(runtime.ConfigPath, Revision(json), json);
        }
        finally
        {
            _writeLock.Release();
        }
    }

    internal static string Revision(string json) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(json))).ToLowerInvariant();
}

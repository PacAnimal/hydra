using Cathedral.Extensions;
using Hydra.Config;

namespace Hydra.Management;

internal sealed class TransactionalConfigStore(HydraRuntimeInfo runtime)
{
    private readonly SemaphoreSlim _writeLock = new(1, 1);
    private readonly ConfigDir _dir = new(runtime.ConfigPath);

    /// <summary>
    /// Reads under the cross-process config lock — the TUI is a SEPARATE PROCESS from the service and both
    /// read this file, so an unlocked read here is a read racing another program's save. On Windows it does
    /// not merely read a stale copy: the reader's handle refuses the saver's replace and the SAVE fails, so
    /// a user's config edit is lost because something else happened to be reading.
    ///
    /// <para><b>This lock has no portable test.</b> Its only symptom is a refused replace, which is Windows
    /// behaviour — a POSIX rename ignores open handles. The save side's lock is covered everywhere by
    /// <c>ConcurrentSavesLoseNoEdit</c>, which is no cover for this one.</para>
    /// </summary>
    internal async Task<ConfigDocument> ReadAsync(CancellationToken cancel = default)
    {
        await using var fileLock = await ConfigFileLock.Acquire(_dir.ConfigLock, cancel);
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
            await using var fileLock = await ConfigFileLock.Acquire(_dir.ConfigLock, cancel);

            var current = await File.ReadAllTextAsync(runtime.ConfigPath, cancel);
            if (!Revision(current).Equals(expectedRevision, StringComparison.Ordinal))
                throw new InvalidOperationException("hydra.conf changed outside the TUI. Reload before saving.");

            await PrivateFile.Write(runtime.ConfigPath, json, null, cancel);

            // Re-parsed from disk: the content was validated above, so what is left to catch is bytes that
            // did not survive the trip, and a failure puts the old config back so an unloadable write never
            // stays hydra.conf.
            try
            {
                _ = HydraConfigFile.Parse(await File.ReadAllTextAsync(runtime.ConfigPath, cancel), runtime.ConfigPath);
            }
            catch
            {
                // NOT the caller's token: a rollback that puts the previous config back is not something to
                // abandon because whoever asked has stopped waiting. The alternative is leaving hydra.conf
                // holding content we just decided was unloadable.
                await PrivateFile.Write(runtime.ConfigPath, current, null, CancellationToken.None);
                throw;
            }

            return new ConfigDocument(runtime.ConfigPath, Revision(json), json);
        }
        finally
        {
            _writeLock.Release();
        }
    }

    internal static string Revision(string json) => json.GetSha256Hash();
}

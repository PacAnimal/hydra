using Cathedral.Extensions;
using Hydra.Config;

namespace Hydra.Management;

internal sealed class TransactionalConfigStore(HydraRuntimeInfo runtime)
{
    private readonly SemaphoreSlim _writeLock = new(1, 1);
    private readonly ConfigDir _dir = new(runtime.ConfigPath);

    // reads take the cross-process lock too (see ConfigFileLock)
    internal async Task<ConfigDocument> Read(CancellationToken cancel = default)
    {
        await using var fileLock = await ConfigFileLock.Acquire(_dir.ConfigLock, cancel);
        return await ReadUnlocked(cancel);
    }

    // the same read for a caller already holding the config lock, which is not re-entrant (rollback)
    internal async Task<ConfigDocument> ReadUnlocked(CancellationToken cancel = default)
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

    internal async Task<ConfigDocument> Save(string expectedRevision, string json, CancellationToken cancel,
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

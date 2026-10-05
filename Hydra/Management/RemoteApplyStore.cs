using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Hydra.Management;

internal sealed class RemoteApplyStore(
    HydraRuntimeInfo runtime,
    TransactionalConfigStore config,
    IHydraLifetimeController lifetime,
    ILogger<RemoteApplyStore> log,
    TimeSpan? confirmationWindow = null,
    TimeSpan? rollbackRetryDelay = null) : BackgroundService
{
    internal static readonly TimeSpan ConfirmationWindow = TimeSpan.FromSeconds(90);
    private readonly TimeSpan _confirmationWindow = confirmationWindow ?? ConfirmationWindow;
    private readonly TimeSpan _rollbackRetryDelay = rollbackRetryDelay ?? TimeSpan.FromSeconds(5);
    private readonly SemaphoreSlim _lock = new(1, 1);
    private readonly ConfigDir _dir = new(runtime.ConfigPath);

    /// <summary>
    /// Does not hold the config lock across its read and save: <c>SaveAsync</c> re-checks the revision under
    /// that lock, so a config that changed in between is refused there. Holding it here would deadlock, since
    /// the calls it makes take it themselves and it is not re-entrant.
    /// </summary>
    internal async Task<RemoteApplyAccepted> BeginAsync(string expectedRevision, string maskedJson, CancellationToken cancel)
    {
        await _lock.WaitAsync(cancel);
        try
        {
            if (File.Exists(_dir.RemoteApplyMarker))
                throw new InvalidOperationException("A remote configuration transaction is already awaiting confirmation or rollback.");
            if (new FileInfo(runtime.ConfigPath).LinkTarget != null)
                throw new IOException("Remote configuration apply refuses a symbolic-link config path.");

            var current = await config.ReadAsync(cancel);
            if (!current.Revision.Equals(expectedRevision, StringComparison.Ordinal))
                throw new InvalidOperationException("The remote configuration changed. Reload it before applying edits.");
            var candidateJson = ConfigSecretMask.Restore(maskedJson, current.Json);
            var validation = TransactionalConfigStore.Validate(candidateJson);
            if (!validation.Valid) throw new InvalidOperationException(validation.Error);

            var risky = RemoteConnectivityGuard.FindRiskyChanges(current.Json, candidateJson);
            if (risky.Count > 0)
                throw new InvalidOperationException($"This first remote-apply version requires local access for connectivity changes: {string.Join(", ", risky)}.");

            var transactionId = Guid.NewGuid();
            var candidateRevision = TransactionalConfigStore.Revision(candidateJson);
            var marker = new RemoteApplyMarker(transactionId, candidateRevision, current.Revision,
                DateTimeOffset.UtcNow + _confirmationWindow);

            await PrivateFile.Write(_dir.RemoteApplyBackup, current.Json, PrivateFile.OwnerOnly, cancel);
            var persistedBackup = await File.ReadAllTextAsync(_dir.RemoteApplyBackup, cancel);
            if (!TransactionalConfigStore.Revision(persistedBackup).Equals(current.Revision, StringComparison.Ordinal))
                throw new IOException("Remote configuration backup verification failed.");
            await PrivateFile.Write(_dir.RemoteApplyMarker, ManagementJson.Serialize(marker), PrivateFile.OwnerOnly, cancel);
            try
            {
                _ = await config.SaveAsync(expectedRevision, candidateJson, cancel, allowPendingRemoteApply: true);
            }
            catch
            {
                DeleteTransactionFiles(_dir);
                throw;
            }

            return new RemoteApplyAccepted(transactionId, candidateRevision, marker.ExpiresAt,
                "Candidate saved. Hydra will restart and roll back unless the controller confirms the new revision.");
        }
        finally { _lock.Release(); }
    }

    internal async Task<RemoteApplyState?> GetStateAsync(CancellationToken cancel = default)
    {
        await _lock.WaitAsync(cancel);
        try
        {
            var marker = await ReadMarkerAsync(cancel);
            return marker == null ? null : new RemoteApplyState(marker.TransactionId, marker.CandidateRevision, marker.ExpiresAt);
        }
        finally { _lock.Release(); }
    }

    internal async Task ConfirmAsync(Guid transactionId, string expectedRevision, CancellationToken cancel)
    {
        await _lock.WaitAsync(cancel);
        try
        {
            var marker = await ReadMarkerAsync(cancel)
                ?? throw new InvalidOperationException("No remote configuration transaction is awaiting confirmation.");
            if (marker.TransactionId != transactionId || !marker.CandidateRevision.Equals(expectedRevision, StringComparison.Ordinal))
                throw new InvalidOperationException("Remote configuration confirmation does not match the active transaction.");
            var current = await config.ReadAsync(cancel);
            if (!current.Revision.Equals(marker.CandidateRevision, StringComparison.Ordinal))
                throw new InvalidOperationException("The candidate revision is not active and cannot be confirmed.");
            DeleteTransactionFiles(_dir);
        }
        finally { _lock.Release(); }
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(1));
        while (await timer.WaitForNextTickAsync(stoppingToken))
        {
            var marker = await GetStateAsync(stoppingToken);
            if (marker == null || marker.ExpiresAt > DateTimeOffset.UtcNow) continue;
            try
            {
                await RollbackAsync(stoppingToken);
                log.LogWarning("Remote configuration was not confirmed; restored the last-known-good config and restarting Hydra");
                lifetime.RestartAfterResponse();
                return;
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                return;
            }
            catch (Exception ex)
            {
                if (log.IsEnabled(LogLevel.Critical))
                    log.LogCritical(ex, "Remote configuration rollback failed; retrying in {Delay}", _rollbackRetryDelay);
                await Task.Delay(_rollbackRetryDelay, stoppingToken);
            }
        }
    }

    internal async Task RollbackAsync(CancellationToken cancel)
    {
        await _lock.WaitAsync(cancel);
        try
        {
            // The config lock spans the revision check AND the overwrite: rollback replaces hydra.conf, and
            // the TUI in another process saves the same file under the same lock. ReadUnlockedAsync because
            // the lock is not re-entrant and we are holding it.
            await using var fileLock = await ConfigFileLock.Acquire(_dir.ConfigLock, cancel);

            if (!File.Exists(_dir.RemoteApplyBackup))
                throw new IOException("Remote configuration backup is missing; automatic rollback cannot continue.");
            var marker = await ReadMarkerAsync(cancel)
                ?? throw new IOException("Remote configuration marker is missing; automatic rollback cannot continue.");
            var current = await config.ReadUnlockedAsync(cancel);
            var outcome = await TryRestoreCore(_dir, runtime.ConfigPath, marker, current.Revision, cancel);
            switch (outcome.Result)
            {
                case RestoreResult.Refused:
                    throw new IOException("hydra.conf changed outside the active remote transaction; automatic rollback refused to overwrite it.");
                case RestoreResult.InvalidBackup:
                    throw new InvalidOperationException($"Remote configuration backup is invalid: {outcome.Error}");
            }
        }
        finally { _lock.Release(); }
    }

    /// <summary>
    /// The same restore as <see cref="RollbackAsync"/>, before anything else is running, and it never throws
    /// over the transaction: false leaves startup to load whatever is there. It differs where startup must:
    /// it checks expiry itself, which the running store leaves to its timer, and it accepts a MISSING
    /// hydra.conf, since the backup is then the only config there is.
    /// </summary>
    internal static async Task<bool> RestoreExpiredBeforeStartupAsync(string configPath, CancellationToken cancel = default)
    {
        var dir = new ConfigDir(configPath);
        if (!File.Exists(dir.RemoteApplyMarker)) return false;
        // Startup, but not alone: a TUI may already be running against this config directory.
        await using var fileLock = await ConfigFileLock.Acquire(dir.ConfigLock, cancel);
        RemoteApplyMarker marker;
        try { marker = ManagementJson.Deserialize<RemoteApplyMarker>(await File.ReadAllTextAsync(dir.RemoteApplyMarker, cancel)); }
        catch { return false; }
        if (marker.ExpiresAt > DateTimeOffset.UtcNow) return false;
        if (!File.Exists(dir.RemoteApplyBackup)) return false;
        var currentRevision = File.Exists(configPath)
            ? TransactionalConfigStore.Revision(await File.ReadAllTextAsync(configPath, cancel))
            : null;
        var outcome = await TryRestoreCore(dir, configPath, marker, currentRevision, cancel);
        return outcome.Result is RestoreResult.Restored or RestoreResult.AlreadyPrevious;
    }

    /// <summary>
    /// Puts the backup back over the candidate and ends the transaction. The caller holds the config lock and
    /// has checked the backup exists; a null <paramref name="currentRevision"/> means hydra.conf is missing.
    /// </summary>
    private static async Task<RestoreOutcome> TryRestoreCore(ConfigDir dir, string configPath, RemoteApplyMarker marker,
        string? currentRevision, CancellationToken cancel)
    {
        if (currentRevision?.Equals(marker.PreviousRevision, StringComparison.Ordinal) == true)
        {
            DeleteTransactionFiles(dir);
            return new RestoreOutcome(RestoreResult.AlreadyPrevious);
        }
        if (currentRevision != null && !currentRevision.Equals(marker.CandidateRevision, StringComparison.Ordinal))
            return new RestoreOutcome(RestoreResult.Refused);
        var backup = await File.ReadAllTextAsync(dir.RemoteApplyBackup, cancel);
        var validation = TransactionalConfigStore.Validate(backup);
        if (!validation.Valid) return new RestoreOutcome(RestoreResult.InvalidBackup, validation.Error);
        await PrivateFile.Write(configPath, backup, null, cancel);
        DeleteTransactionFiles(dir);
        return new RestoreOutcome(RestoreResult.Restored);
    }

    private async Task<RemoteApplyMarker?> ReadMarkerAsync(CancellationToken cancel)
    {
        if (!File.Exists(_dir.RemoteApplyMarker)) return null;
        return ManagementJson.Deserialize<RemoteApplyMarker>(await File.ReadAllTextAsync(_dir.RemoteApplyMarker, cancel));
    }

    // File.Delete is a no-op for a file that is not there
    private static void DeleteTransactionFiles(ConfigDir dir)
    {
        File.Delete(dir.RemoteApplyMarker);
        File.Delete(dir.RemoteApplyBackup);
    }

    internal static bool HasPendingTransaction(string configPath) => File.Exists(new ConfigDir(configPath).RemoteApplyMarker);

    private enum RestoreResult { Restored, AlreadyPrevious, Refused, InvalidBackup }

    private sealed record RestoreOutcome(RestoreResult Result, string? Error = null);
}

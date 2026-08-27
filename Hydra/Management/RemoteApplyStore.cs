using System.Text;
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
    private string MarkerPath => MarkerPathFor(runtime.ConfigPath);
    private string BackupPath => BackupPathFor(runtime.ConfigPath);

    internal async Task<RemoteApplyAccepted> BeginAsync(string expectedRevision, string maskedJson, CancellationToken cancel)
    {
        await _lock.WaitAsync(cancel);
        try
        {
            if (File.Exists(MarkerPath))
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

            await WritePrivateAsync(BackupPath, current.Json,
                UnixFileMode.UserRead | UnixFileMode.UserWrite, cancel);
            var persistedBackup = await File.ReadAllTextAsync(BackupPath, cancel);
            if (!TransactionalConfigStore.Revision(persistedBackup).Equals(current.Revision, StringComparison.Ordinal))
                throw new IOException("Remote configuration backup verification failed.");
            await WritePrivateAsync(MarkerPath, ManagementJson.Serialize(marker),
                UnixFileMode.UserRead | UnixFileMode.UserWrite, cancel);
            try
            {
                _ = await config.SaveAsync(expectedRevision, candidateJson, cancel, allowPendingRemoteApply: true);
            }
            catch
            {
                DeleteTransactionFiles();
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
            DeleteTransactionFiles();
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
            if (!File.Exists(BackupPath))
                throw new IOException("Remote configuration backup is missing; automatic rollback cannot continue.");
            var marker = await ReadMarkerAsync(cancel)
                ?? throw new IOException("Remote configuration marker is missing; automatic rollback cannot continue.");
            var current = await config.ReadAsync(cancel);
            if (current.Revision.Equals(marker.PreviousRevision, StringComparison.Ordinal))
            {
                DeleteTransactionFiles();
                return;
            }
            if (!current.Revision.Equals(marker.CandidateRevision, StringComparison.Ordinal))
                throw new IOException("hydra.conf changed outside the active remote transaction; automatic rollback refused to overwrite it.");
            var backup = await File.ReadAllTextAsync(BackupPath, cancel);
            var validation = TransactionalConfigStore.Validate(backup);
            if (!validation.Valid) throw new InvalidOperationException($"Remote configuration backup is invalid: {validation.Error}");
            await WritePrivateAsync(runtime.ConfigPath, backup, ConfigMode(), cancel);
            DeleteTransactionFiles();
        }
        finally { _lock.Release(); }
    }

    internal static async Task<bool> RestoreExpiredBeforeStartupAsync(string configPath, CancellationToken cancel = default)
    {
        var markerPath = MarkerPathFor(configPath);
        if (!File.Exists(markerPath)) return false;
        RemoteApplyMarker marker;
        try { marker = ManagementJson.Deserialize<RemoteApplyMarker>(await File.ReadAllTextAsync(markerPath, cancel)); }
        catch { return false; }
        if (marker.ExpiresAt > DateTimeOffset.UtcNow) return false;
        var backupPath = BackupPathFor(configPath);
        if (!File.Exists(backupPath)) return false;
        string? currentJson = File.Exists(configPath)
            ? await File.ReadAllTextAsync(configPath, cancel)
            : null;
        var currentRevision = currentJson == null ? null : TransactionalConfigStore.Revision(currentJson);
        if (currentRevision?.Equals(marker.PreviousRevision, StringComparison.Ordinal) == true)
        {
            File.Delete(markerPath);
            File.Delete(backupPath);
            return true;
        }
        if (currentRevision != null && !currentRevision.Equals(marker.CandidateRevision, StringComparison.Ordinal)) return false;
        var backup = await File.ReadAllTextAsync(backupPath, cancel);
        var validation = TransactionalConfigStore.Validate(backup);
        if (!validation.Valid) return false;
        var mode = !OperatingSystem.IsWindows() && File.Exists(configPath)
            ? File.GetUnixFileMode(configPath)
            : UnixFileMode.UserRead | UnixFileMode.UserWrite;
        await WritePrivateAsync(configPath, backup, mode, cancel);
        File.Delete(markerPath);
        File.Delete(backupPath);
        return true;
    }

    private async Task<RemoteApplyMarker?> ReadMarkerAsync(CancellationToken cancel)
    {
        if (!File.Exists(MarkerPath)) return null;
        return ManagementJson.Deserialize<RemoteApplyMarker>(await File.ReadAllTextAsync(MarkerPath, cancel));
    }

    private UnixFileMode ConfigMode() => !OperatingSystem.IsWindows()
        ? File.GetUnixFileMode(runtime.ConfigPath)
        : UnixFileMode.UserRead | UnixFileMode.UserWrite;

    private void DeleteTransactionFiles()
    {
        if (File.Exists(MarkerPath)) File.Delete(MarkerPath);
        if (File.Exists(BackupPath)) File.Delete(BackupPath);
    }

    private static string MarkerPathFor(string configPath) => Path.Combine(Path.GetDirectoryName(Path.GetFullPath(configPath))!, ".hydra-remote-apply.json");
    private static string BackupPathFor(string configPath) => Path.Combine(Path.GetDirectoryName(Path.GetFullPath(configPath))!, ".hydra-remote-backup.conf");
    internal static bool HasPendingTransaction(string configPath) => File.Exists(MarkerPathFor(configPath));

    private static async Task WritePrivateAsync(string path, string content, UnixFileMode mode, CancellationToken cancel)
    {
        var directory = Path.GetDirectoryName(path)!;
        var temp = Path.Combine(directory, $".{Path.GetFileName(path)}.{Guid.NewGuid():N}.tmp");
        try
        {
            var bytes = new UTF8Encoding(false).GetBytes(content);
            await using (var stream = new FileStream(temp, FileMode.CreateNew, FileAccess.Write, FileShare.None,
                             4096, FileOptions.Asynchronous | FileOptions.WriteThrough))
            {
                await stream.WriteAsync(bytes, cancel);
                await stream.FlushAsync(cancel);
                stream.Flush(flushToDisk: true);
            }
            if (!OperatingSystem.IsWindows()) File.SetUnixFileMode(temp, mode);
            File.Move(temp, path, true);
        }
        finally
        {
            if (File.Exists(temp)) File.Delete(temp);
        }
    }
}

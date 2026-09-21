namespace Hydra.Management;

/// <summary>
/// The cross-process lock the config-directory stores serialise on.
///
/// <para><b>Cross-process is the whole point, and it is easy to think it is not needed.</b> The TUI is a
/// SEPARATE PROCESS from the service, and both read and write the same files — so an in-process
/// <c>SemaphoreSlim</c> guards against nothing that actually happens. A reader that skips this makes a
/// concurrent writer FAIL on Windows: the reader's handle carries no <c>FILE_SHARE_DELETE</c>, so the
/// writer's replace is refused and the write is lost, silently, while POSIX hides the same defect entirely
/// by letting the rename succeed and the reader finish off the old inode.</para>
///
/// <para>So READS take it too, not just writes. That is the half that is easy to leave out, and it is the
/// half that was left out.</para>
/// </summary>
internal static class ConfigFileLock
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan Poll = TimeSpan.FromMilliseconds(25);

    /// <summary>The lock guarding the remote-management state beside a config file.</summary>
    internal static string ManagementPathFor(string directory) => Path.Combine(directory, ".hydra-management.lock");

    /// <summary>
    /// The lock guarding <c>hydra.conf</c> and everything that rewrites it — the TUI's save, a remote apply
    /// and its rollback. ONE lock for all of them, because they contend over the same file and a lock per
    /// store would let two of them replace it at once.
    /// </summary>
    internal static string ConfigPathFor(string configPath) =>
        Path.Combine(Path.GetDirectoryName(Path.GetFullPath(configPath))!, ".hydra-config.lock");

    /// <summary>
    /// Takes the lock, waiting for a holder and giving up by THROWING once the budget is gone — a caller
    /// that cannot get it must not proceed to read or write the thing it guards.
    /// </summary>
    internal static async Task<FileStream> Acquire(string lockPath, CancellationToken cancel)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(lockPath)!);
        var deadline = DateTimeOffset.UtcNow + Timeout;
        while (true)
        {
            cancel.ThrowIfCancellationRequested();
            if (File.Exists(lockPath) && new FileInfo(lockPath).LinkTarget != null)
                throw new IOException($"Hydra lock {Path.GetFileName(lockPath)} cannot be a symbolic link.");
            try
            {
                var stream = new FileStream(lockPath, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None, 1, FileOptions.Asynchronous);
                if (!OperatingSystem.IsWindows())
                    File.SetUnixFileMode(lockPath, UnixFileMode.UserRead | UnixFileMode.UserWrite);
                return stream;
            }
            catch (IOException) when (DateTimeOffset.UtcNow < deadline)
            {
                await Task.Delay(Poll, cancel);
            }
        }
    }
}

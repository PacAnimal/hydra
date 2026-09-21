using System.Collections.Concurrent;

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
    private static readonly TimeSpan Budget = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan Poll = TimeSpan.FromMilliseconds(25);

    /// <summary>
    /// The lock files this PROCESS currently holds, so a wait that runs out can say which of the two very
    /// different things went wrong.
    ///
    /// <para><b>The lock is exclusive and not re-entrant.</b> Taking it while already holding it waits for
    /// itself for the whole budget and then reports "the process cannot access the file" — five seconds
    /// ending in a message that names no lock, no budget and no culprit. That has happened here once
    /// already, when rollback called a read that took the lock rollback was holding, and two more call
    /// sites sit one edit away from it.</para>
    ///
    /// <para>An <c>AsyncLocal</c> was tried first and does not work: a value written inside <c>Acquire</c>
    /// flows to ITS children, never back to the caller that asked for the lock, so the guard could never
    /// see a hold it had itself recorded. What a process CAN know is whether the lock is its own, and that
    /// is the difference between "somebody else is busy" and "you are waiting for yourself".</para>
    /// </summary>
    private static readonly ConcurrentDictionary<string, byte> HeldHere = new();

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
    internal static Task<IAsyncDisposable> Acquire(string lockPath, CancellationToken cancel) =>
        Acquire(lockPath, Budget, cancel);

    /// <param name="lockPath">The lock file to take; see the two path helpers above.</param>
    /// <param name="budget">How long to wait. Only a test passes this; everything else takes <c>Budget</c>.</param>
    /// <param name="cancel">Abandons the wait; the lock is not taken.</param>
    internal static async Task<IAsyncDisposable> Acquire(string lockPath, TimeSpan budget, CancellationToken cancel)
    {
        var full = Path.GetFullPath(lockPath);
        Directory.CreateDirectory(Path.GetDirectoryName(full)!);
        var deadline = DateTimeOffset.UtcNow + budget;
        while (true)
        {
            cancel.ThrowIfCancellationRequested();
            if (File.Exists(full) && new FileInfo(full).LinkTarget != null)
                throw new IOException($"Hydra lock {Path.GetFileName(full)} cannot be a symbolic link.");

            FileStream? stream = null;
            try
            {
                stream = new FileStream(full, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None, 1, FileOptions.Asynchronous);
                // Through the HANDLE, not the path. A path chmod follows a symlink, which is the very thing
                // the guard above tries to refuse and cannot do reliably — File.Exists follows a link too,
                // so a DANGLING one reads as absent and slips straight past it. fchmod cannot be redirected.
                if (!OperatingSystem.IsWindows())
                    File.SetUnixFileMode(stream.SafeFileHandle, UnixFileMode.UserRead | UnixFileMode.UserWrite);

                HeldHere[full] = 0;
                return new Holder(stream, full);
            }
            catch (IOException)
            {
                // Disposed here or the handle leaks until finalisation, still holding the lock.
                if (stream != null) await stream.DisposeAsync();
                // The deadline is decided BELOW, not in an exception filter. Evaluated in the filter, an
                // IOException arriving just as the budget expired matched neither this clause nor the
                // named throws, and escaped as the raw "the process cannot access the file" — under
                // exactly the load that makes the diagnosis worth having.
                if (DateTimeOffset.UtcNow < deadline) await Task.Delay(Poll, cancel);
            }
            catch
            {
                if (stream != null) await stream.DisposeAsync();
                throw;
            }

            if (DateTimeOffset.UtcNow < deadline) continue;

            // WHICH failure this is matters more than the fact of it. Our own process holding the lock is
            // a bug on this call stack — almost always a path that took it and then called something that
            // takes it again — and is fixed in the code. Another process holding it is a busy machine.
            // Careful what this claims. A process-wide record cannot tell re-entrancy from two honest
            // callers here contending — and contention is the COMMON case (a status poll every couple of
            // seconds against a save), while re-entrancy is rare. Naming only the rare one would be a false
            // accusation most of the times it fired, and a false instruction with it.
            if (HeldHere.ContainsKey(full))
                throw new TimeoutException(
                    $"Waited {budget.TotalSeconds:0.##}s for the Hydra lock {Path.GetFileName(full)}, which THIS process holds on some stack. " +
                    "Either another operation here is still working, or this call stack is waiting for a lock it already took — " +
                    "the lock is not re-entrant, and a caller that holds it must use the Unlocked read instead.");

            throw new TimeoutException(
                $"Waited {budget.TotalSeconds:0.##}s for the Hydra lock {Path.GetFileName(full)} and another process still holds it. " +
                "A configuration read or write is in progress elsewhere.");
        }
    }

    /// <summary>Releases the file AND forgets the path, so the re-entrancy guard does not outlive the hold.</summary>
    private sealed class Holder(FileStream stream, string full) : IAsyncDisposable
    {
        public async ValueTask DisposeAsync()
        {
            HeldHere.TryRemove(full, out _);
            await stream.DisposeAsync();
        }
    }
}

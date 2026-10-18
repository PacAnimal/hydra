using System.Collections.Concurrent;

namespace Hydra.Management;

// The cross-process lock the config-directory stores serialise on: the TUI and the daemon are separate processes.
// Reads take it too. On Windows a reader's handle lacks FILE_SHARE_DELETE, so a concurrent writer's replace is
// refused and its write lost; POSIX lets the rename succeed, so this has no portable test.
internal static class ConfigFileLock
{
    private static readonly TimeSpan Budget = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan Poll = TimeSpan.FromMilliseconds(25);

    // the lock files this process holds, so a timeout can tell our own hold (the lock is not re-entrant) from another's
    private static readonly ConcurrentDictionary<string, byte> HeldHere = new();

    // takes the lock, throwing once the budget is gone: a caller without it must not touch what it guards
    internal static Task<IAsyncDisposable> Acquire(string lockPath, CancellationToken cancel) =>
        Acquire(lockPath, Budget, cancel);

    // only a test passes its own budget
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
                // through the handle: a path chmod follows a symlink, and a dangling one slips past the guard above
                if (!OperatingSystem.IsWindows())
                    File.SetUnixFileMode(stream.SafeFileHandle, UnixFileMode.UserRead | UnixFileMode.UserWrite);

                HeldHere[full] = 0;
                return new Holder(stream, full);
            }
            catch (IOException)
            {
                // or the handle leaks until finalisation, still holding the lock
                if (stream != null) await stream.DisposeAsync();
                // the deadline is decided below, not in a filter, so a late IOException still gets a named throw
                if (DateTimeOffset.UtcNow < deadline) await Task.Delay(Poll, cancel);
            }
            catch
            {
                if (stream != null) await stream.DisposeAsync();
                throw;
            }

            if (DateTimeOffset.UtcNow < deadline) continue;

            // held here may be re-entrancy or honest contention between our own callers, so name both
            if (HeldHere.ContainsKey(full))
                throw new TimeoutException(
                    $"Waited {budget.TotalSeconds:0.##}s for the Hydra lock {Path.GetFileName(full)}, which THIS process holds on some stack. " +
                    "Either another operation here is still working, or this call stack is waiting for a lock it already took — " +
                    "the lock is not re-entrant, and a caller that holds it must use the Unlocked read instead.");

            // not "another process holds it": a disk returning EIO ends here too
            throw new TimeoutException(
                $"Waited {budget.TotalSeconds:0.##}s for the Hydra lock {Path.GetFileName(full)} and it is still not available. " +
                "Another process is reading or writing the configuration, or the filesystem is refusing the lock file.");
        }
    }

    // releases the file and forgets the path, so the re-entrancy guard does not outlive the hold
    private sealed class Holder(FileStream stream, string full) : IAsyncDisposable
    {
        public async ValueTask DisposeAsync()
        {
            HeldHere.TryRemove(full, out _);
            await stream.DisposeAsync();
        }
    }
}

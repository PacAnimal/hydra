using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Text;

namespace Hydra.Platform;

// exclusive process-level lock backed by a file.
// uses FileShare.None so .NET applies the platform-appropriate exclusive lock:
// flock(LOCK_EX) on linux, open(O_EXLOCK) on macOS, LockFileEx on windows.
// on unix, O_EXLOCK/flock is advisory for plain open() calls, so we read the
// PID back via raw syscalls that bypass .NET's locking logic.
// file descriptors have O_CLOEXEC set by .NET, so execv() releases the lock
// cleanly and the new process image re-acquires it on startup.
internal sealed partial class ProcessLock : IDisposable
{
    private readonly FileStream _stream;

    private ProcessLock(FileStream stream) => _stream = stream;

    public void Dispose() => _stream.Dispose();

    public static ProcessLock Acquire(string path)
    {
        FileStream stream;
        try
        {
            stream = new FileStream(path, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
        }
        catch (IOException)
        {
            throw LockError(path, TryReadPid(path));
        }

        stream.SetLength(0);
        using var writer = new StreamWriter(stream, leaveOpen: true);
        writer.Write(Environment.ProcessId);
        writer.Flush();
        stream.Flush();

        return new ProcessLock(stream);
    }

    // internal for testing
    internal static int? TryReadPid(string path)
    {
        if (OperatingSystem.IsWindows())
            return null; // FileShare.None prevents reads on windows

        if (OperatingSystem.IsLinux() || OperatingSystem.IsMacOS())
            return TryReadPidUnix(path);

        return null;
    }

    [SupportedOSPlatform("linux")]
    [SupportedOSPlatform("macos")]
    private static int? TryReadPidUnix(string path)
    {
        // use raw syscalls to bypass .NET's O_EXLOCK/flock logic — plain open() always
        // succeeds on unix regardless of any advisory lock held by another process
        const int oRdOnly = 0;
        int fd = Open(path, oRdOnly);
        if (fd < 0) return null;
        try
        {
            var buf = new byte[32];
            var len = Read(fd, buf, buf.Length);
            if (len <= 0) return null;
            var text = Encoding.UTF8.GetString(buf, 0, (int)len).Trim();
            return int.TryParse(text, out var pid) ? pid : null;
        }
        finally
        {
            Close(fd);
        }
    }

    private static InvalidOperationException LockError(string path, int? pid) =>
        pid.HasValue
            ? new InvalidOperationException($"Another Hydra instance is already running (PID: {pid}). Lock file: {path}")
            : new InvalidOperationException($"Another Hydra instance is already running. Lock file: {path}");

    [SupportedOSPlatform("linux")]
    [SupportedOSPlatform("macos")]
    [LibraryImport("libc", EntryPoint = "open", SetLastError = true, StringMarshalling = StringMarshalling.Utf8)]
    private static partial int Open(string path, int flags);

    [SupportedOSPlatform("linux")]
    [SupportedOSPlatform("macos")]
    [LibraryImport("libc", EntryPoint = "read", SetLastError = true)]
    private static partial nint Read(int fd, byte[] buf, nint count);

    [SupportedOSPlatform("linux")]
    [SupportedOSPlatform("macos")]
    [LibraryImport("libc", EntryPoint = "close", SetLastError = true)]
    private static partial int Close(int fd);
}

using System.Security.Principal;
using Cathedral.Utils;
using Hydra.Platform.Windows;

namespace Hydra.Management;

/// <summary>
/// Writes a file in the config directory the one way all of them are written: durably, and private to the
/// accounts Hydra trusts with its secrets unless an admin has said otherwise.
///
/// <para>Every write is Cathedral's durable replace — a temp whose access control is set at creation, fsynced
/// before anything promotes it, the parent directory fsynced, a transient holder waited out.</para>
///
/// <para><b>The mode, on Unix:</b> an explicit one is stamped on the file, new or not. A null one keeps an
/// EXISTING file's mode exactly — an admin's 0644 <c>hydra.conf</c> stays 0644, their 0400 stays 0400 —
/// and makes a NEW file <see cref="OwnerOnly"/>.</para>
///
/// <para><b>On Windows</b> only an owner-only mode means private: the file, new or not, gets a protected DACL
/// granting the writer, the console user, SYSTEM and Administrators, and nothing from the directory. The
/// console user is there because the service and an unelevated <c>hydra pair</c> write the same sidecar,
/// and each write replaces the DACL. A null or broader mode keeps an existing file's DACL and gives a new one
/// the directory's, so a <c>hydra.conf</c> the SYSTEM service recreates stays readable to whoever could read
/// it before.</para>
/// </summary>
internal static class PrivateFile
{
    internal const UnixFileMode OwnerOnly = UnixFileMode.UserRead | UnixFileMode.UserWrite;

    private const UnixFileMode OwnerBits = UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute;

    internal static Task Write(string path, string content, UnixFileMode? mode, CancellationToken cancel) =>
        Write(path, content, mode, ConsoleUser, cancel);

    internal static async Task Write(string path, string content, UnixFileMode? mode, Func<SecurityIdentifier?> consoleUser, CancellationToken cancel)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
        var createSecurity = OperatingSystem.IsWindows() && mode is { } explicitMode && (explicitMode & ~OwnerBits) == 0
            ? new WindowsPrivateAccess(consoleUser() is { } user ? [user] : null)
            : null;
        await FileSafe.Write(new DiskFileHandler(path, mode, newFileMode: OwnerOnly, createSecurity: createSecurity), content, cancel);
    }

    private static SecurityIdentifier? ConsoleUser() => OperatingSystem.IsWindows() ? Win32Session.ActiveConsoleUser() : null;
}

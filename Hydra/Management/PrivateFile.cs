using Cathedral.Utils;

namespace Hydra.Management;

/// <summary>
/// Writes a file in the config directory the one way all of them are written.
///
/// <para><b>Cathedral's <see cref="FileSafe"/> does the hard part</b> — content into a temp file, fsynced
/// before anything promotes it, renamed over the destination, and the PARENT DIRECTORY fsynced so the
/// rename itself survives power loss. Three hand-rolled copies of this lived here and none of them fsynced
/// the directory, so a crash could leave the old file back. It also retries the rename, which is what a
/// virus scanner or indexer holding either file on Windows requires, and reads the same way.</para>
///
/// <para>The MODE is applied afterwards because a rename takes the temp file's permissions, and the temp is
/// created with the process umask. There is therefore a brief window where a new file exists at the default
/// mode before it is tightened — small, and worth removing by teaching <c>DiskFileHandler</c> to create its
/// temp private, which is a Cathedral change rather than one that can be made here.</para>
/// </summary>
internal static class PrivateFile
{
    internal static async Task Write(string path, string content, UnixFileMode mode, CancellationToken cancel)
    {
        await FileSafe.Write(new DiskFileHandler(path), content, cancel);
        if (!OperatingSystem.IsWindows()) File.SetUnixFileMode(path, mode);
    }
}

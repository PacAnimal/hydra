using Cathedral.Utils;

namespace Hydra.Management;

/// <summary>
/// Writes a file in the config directory the one way all of them are written: durably, and readable by
/// nobody else.
///
/// <para><b>Cathedral does all of it</b> — content into a temp whose mode is stamped at <c>open(2)</c> so a
/// complete copy is never briefly world-readable, fsynced before anything promotes it, promoted in a way
/// that keeps the destination's DACL on Windows, the parent directory fsynced so the promotion survives
/// power loss, a transient holder waited out, and the owner handed back where the process may do so.</para>
///
/// <para>This type is now only the house style: one place saying that everything in the config directory is
/// private, so no call site has to remember it. Three stores here each hand-rolled their own write before,
/// and none of them fsynced the directory.</para>
/// </summary>
internal static class PrivateFile
{
    internal static Task Write(string path, string content, UnixFileMode mode, CancellationToken cancel)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
        return FileSafe.Write(new DiskFileHandler(path, mode), content, cancel);
    }
}

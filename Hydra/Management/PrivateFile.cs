using Cathedral.Utils;

namespace Hydra.Management;

/// <summary>
/// Writes a file in the config directory the one way all of them are written: durably, and never
/// readable by anyone else — not even for an instant, and not if the machine dies mid-write.
///
/// <para><b>Cathedral's <see cref="FileSafe"/> does the durability</b> — content into a temp file, fsynced
/// before anything promotes it, renamed over the destination, and the PARENT DIRECTORY fsynced so the
/// rename itself survives power loss. Three hand-rolled copies of this lived here and none fsynced the
/// directory, so a crash could bring the old file back. It also retries the rename, which is what a virus
/// scanner or indexer holding either file on Windows requires.</para>
///
/// <para><b>The temp file is pre-created at the target mode, and that is the whole point of this type.</b>
/// A rename takes the SOURCE file's permissions, and <c>DiskFileHandler</c> creates its temp with
/// <c>File.Create</c> under the process umask — 0644 on a normal machine. That left two holes, both
/// measured: the destination was 0644 from the rename until a chmod that ran after a directory fsync, and
/// the temp itself was 0644 for the entire write AND SURVIVED A CRASH that way, holding a complete copy.
/// These files hold controller secrets and pairing hashes, under a deterministic name, in a directory
/// <c>HydraConfigFile.ResolvePath</c> puts beside the binary. <c>File.Create</c> TRUNCATES an existing file
/// rather than replacing it, so a temp we create first at 0600 stays 0600 through the write and carries
/// that mode to the destination — no window at either end.</para>
/// </summary>
internal static class PrivateFile
{
    /// <summary>
    /// What <c>DiskFileHandler</c> names its temp file. Depended on deliberately, and pinned by
    /// <c>PrivateFileTests.TheTempFileIsTheOneWePreparedAndItIsPrivate</c> — if Cathedral ever renames it,
    /// the file we prepared is left behind untouched and that test says so, rather than the permissions
    /// quietly going back to 0644.
    /// </summary>
    internal static string TempPathFor(string path) => path + ".tmp";

    internal static async Task Write(string path, string content, UnixFileMode mode, CancellationToken cancel)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);

        if (!OperatingSystem.IsWindows())
        {
            // Created empty at the target mode BEFORE a byte of content exists, so there is no instant at
            // which a complete copy sits at the umask default.
            var temp = TempPathFor(path);
            await using (var _ = new FileStream(temp, FileMode.Create, FileAccess.Write, FileShare.None)) { }
            File.SetUnixFileMode(temp, mode);
        }

        await FileSafe.Write(new DiskFileHandler(path), content, cancel);

        // NO chmod of the destination afterwards, deliberately. One that ran here would always make the
        // destination private and would therefore hide whether the temp ever was — and the temp is the file
        // that holds a complete copy through the whole write and after a crash. With the mode carried by
        // the rename alone, "the destination is 0600" is evidence the temp was, which is what
        // PrivateFileTests asserts.
    }
}

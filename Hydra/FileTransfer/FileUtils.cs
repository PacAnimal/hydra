namespace Hydra.FileTransfer;

internal static class FileUtils
{
    // converts a file:// URL string to a local path; returns null if the URL is not a local file URL
    public static string? FileUrlToLocalPath(string? url)
    {
        if (url == null) return null;
        return Uri.TryCreate(url, UriKind.Absolute, out var uri) && uri.IsFile ? uri.LocalPath : null;
    }

    // moves all entries from tempDir into destDir, auto-renaming on conflict
    public static void MoveTo(string tempDir, string destDir)
    {
        Directory.CreateDirectory(destDir);
        foreach (var entry in Directory.GetFileSystemEntries(tempDir))
        {
            var dest = GetUniquePath(Path.Combine(destDir, Path.GetFileName(entry)));
            if (File.Exists(entry))
                File.Move(entry, dest);
            else if (Directory.Exists(entry))
                MoveDirectory(entry, dest);
        }
    }

    // appends " (1)", " (2)", etc. until a path that doesn't exist is found
    public static string GetUniquePath(string path)
    {
        if (!File.Exists(path) && !Directory.Exists(path)) return path;
        var dir = Path.GetDirectoryName(path) ?? "";
        var nameNoExt = Path.GetFileNameWithoutExtension(path);
        var ext = Path.GetExtension(path);
        for (var i = 1; i <= 10_000; i++)
        {
            var candidate = Path.Combine(dir, $"{nameNoExt} ({i}){ext}");
            if (!File.Exists(candidate) && !Directory.Exists(candidate)) return candidate;
        }
        throw new IOException($"Could not find a unique path for '{path}' after 10,000 attempts");
    }

    private static void MoveDirectory(string src, string dest)
    {
        // Directory.Move fails across volumes; fall back to copy + delete
        try
        {
            Directory.Move(src, dest);
        }
        catch (IOException)
        {
            // only delete the source after a fully successful copy
            CopyDirectory(src, dest);
            try { Directory.Delete(src, recursive: true); }
            catch { /* best effort — temp dir cleanup, not critical */ }
        }
    }

    private static void CopyDirectory(string src, string dest)
    {
        Directory.CreateDirectory(dest);
        foreach (var file in Directory.GetFiles(src))
            File.Copy(file, GetUniquePath(Path.Combine(dest, Path.GetFileName(file))));
        foreach (var dir in Directory.GetDirectories(src))
            CopyDirectory(dir, GetUniquePath(Path.Combine(dest, Path.GetFileName(dir))));
    }
}

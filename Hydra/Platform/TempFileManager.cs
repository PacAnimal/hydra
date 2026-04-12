using System.IO.Compression;
using Microsoft.Extensions.Logging;

namespace Hydra.Platform;

public record TempFileEntry(string Name, string TempPath);

public static class TempFileEntryExtensions
{
    public static List<string> ToPaths(this List<TempFileEntry> files) => [.. files.Select(f => f.TempPath)];
    public static HashSet<string> ToPathSet(this List<TempFileEntry> files) => new(files.Select(f => f.TempPath), StringComparer.OrdinalIgnoreCase);
}

public class TempFileManager : IDisposable
{
    private readonly ILogger<TempFileManager> _log;
    private readonly string _baseDir;
    private readonly Lock _lock = new();

    public TempFileManager(ILogger<TempFileManager> log, string? basePath = null)
    {
        _log = log;
        _baseDir = basePath ?? Path.Combine(Path.GetTempPath(), "Hydra", "clipboard-files");
        _log.LogInformation("Clipboard temp directory: {Path}", _baseDir);
        Cleanup(); // clean up leftovers from previous runs
    }

    // extracts a clipboard zip into the base dir, returns top-level items (files + directories)
    // suitable for passing directly to SetClipboard → CF_HDROP / NSFilenamesPboardType
    public List<TempFileEntry> ExtractZip(byte[] zipData)
    {
        lock (_lock)
        {
            Cleanup();
            Directory.CreateDirectory(_baseDir);

            using var ms = new MemoryStream(zipData);
            using var zip = new ZipArchive(ms, ZipArchiveMode.Read);
            long totalExtracted = 0;
            foreach (var entry in zip.Entries)
            {
                if (string.IsNullOrEmpty(entry.Name)) continue; // directory entry

                // path traversal guard
                var normalized = entry.FullName.Replace('\\', '/');
                if (normalized.Split('/').Any(s => s == "..")) continue;

                var destPath = Path.GetFullPath(Path.Combine(_baseDir, normalized));
                // reject anything that resolves outside the base dir
                if (!destPath.StartsWith(_baseDir + Path.DirectorySeparatorChar, StringComparison.Ordinal)) continue;
                // reject if any already-existing ancestor directory is a symlink (TOCTOU mitigation)
                if (AncestorIsSymlink(_baseDir, destPath)) continue;

                // zip bomb guard: cap total bytes written
                totalExtracted += entry.Length;
                if (totalExtracted > ClipboardUtils.MaxClipboardBytes) break;

                Directory.CreateDirectory(Path.GetDirectoryName(destPath)!);
                using var src = entry.Open();
                using var dst = File.Create(destPath);
                src.CopyTo(dst);
            }

            // return top-level items (immediate children of base dir)
            var result = new List<TempFileEntry>();
            foreach (var path in Directory.GetFileSystemEntries(_baseDir))
                result.Add(new TempFileEntry(Path.GetFileName(path), path));
            return result;
        }
    }

    public void Dispose()
    {
        lock (_lock) Cleanup();
        GC.SuppressFinalize(this);
    }

    // returns true if any directory component between baseDir and destPath is a symlink
    private static bool AncestorIsSymlink(string baseDir, string destPath)
    {
        var dir = Path.GetDirectoryName(destPath);
        while (dir != null && dir.Length > baseDir.Length)
        {
            if (Directory.Exists(dir) && new DirectoryInfo(dir).LinkTarget != null) return true;
            dir = Path.GetDirectoryName(dir);
        }
        return false;
    }

    private void Cleanup()
    {
        if (!Directory.Exists(_baseDir)) return;
        foreach (var entry in Directory.GetFileSystemEntries(_baseDir))
        {
            try
            {
                if (File.Exists(entry)) File.Delete(entry);
                else Directory.Delete(entry, recursive: true);
            }
            catch (Exception ex) { _log.LogDebug(ex, "Failed to delete clipboard temp entry {Entry}", entry); }
        }
    }
}

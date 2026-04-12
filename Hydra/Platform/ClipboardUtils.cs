using System.IO.Compression;
using ByteSizeLib;
using Microsoft.Extensions.Logging;

namespace Hydra.Platform;

public static class ClipboardUtils
{
    public static readonly long MaxClipboardBytes = (long)ByteSize.FromMebiBytes(16).Bytes;

    public static ulong QuickHash(byte[] data)
    {
        // two hashes with different inputs combined into 64-bit to reduce collision probability
        var hc1 = new HashCode();
        hc1.AddBytes(data);
        var hc2 = new HashCode();
        hc2.Add(data.Length); // prefix with length to differentiate from hc1
        hc2.AddBytes(data);
        return ((ulong)(uint)hc1.ToHashCode() << 32) | (uint)hc2.ToHashCode();
    }

    // zips all selected paths (files and/or directories) into a single in-memory archive,
    // preserving directory structure relative to the common parent.
    // returns null if total uncompressed size exceeds the limit, the compressed result exceeds
    // the limit, or nothing could be read.
    public static byte[]? CreateClipboardZip(List<string>? paths, ILogger log)
    {
        if (paths == null || paths.Count == 0) return null;

        // all selected items must share the same parent directory
        var root = Path.GetDirectoryName(paths[0]) ?? paths[0];
        if (paths.Any(p => !string.Equals(Path.GetDirectoryName(p) ?? p, root, StringComparison.Ordinal)))
        {
            log.LogWarning("Clipboard files span multiple directories, refusing to zip");
            return null;
        }

        using var ms = new MemoryStream();
        long totalSize = 0;
        using (var zip = new ZipArchive(ms, ZipArchiveMode.Create, leaveOpen: true))
        {
            foreach (var path in paths)
            {
                IEnumerable<string> filesToAdd;
                if (Directory.Exists(path))
                    filesToAdd = Directory.EnumerateFiles(path, "*", SearchOption.AllDirectories);
                else if (File.Exists(path))
                    filesToAdd = [path];
                else
                    continue;

                foreach (var file in filesToAdd)
                {
                    try
                    {
                        var data = File.ReadAllBytes(file);
                        totalSize += data.Length;
                        if (totalSize > MaxClipboardBytes)
                        {
                            // safe: using disposes the ZipArchive (no finalize needed), ms is disposed by outer using
                            log.LogWarning("Clipboard files too large ({Total} bytes), dropping", totalSize);
                            return null;
                        }
                        var entryName = Path.GetRelativePath(root, file).Replace('\\', '/');
                        var entry = zip.CreateEntry(entryName, CompressionLevel.Optimal);
                        using var stream = entry.Open();
                        stream.Write(data);
                    }
                    catch (Exception ex)
                    {
                        log.LogWarning(ex, "Failed to read clipboard file {Path}, skipping", file);
                    }
                }
            }
        }

        if (totalSize == 0) return null;
        var result = ms.ToArray();
        if (result.Length > MaxClipboardBytes)
        {
            log.LogWarning("Clipboard zip too large when compressed ({Size} bytes), dropping", result.Length);
            return null;
        }
        return result;
    }
}

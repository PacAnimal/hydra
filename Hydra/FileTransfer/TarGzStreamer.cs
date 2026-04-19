using System.Formats.Tar;
using System.IO.Compression;
using System.Security.Cryptography;
using ByteSizeLib;

namespace Hydra.FileTransfer;

public static class TarGzStreamer
{
    public static readonly int ChunkSize = (int)ByteSize.FromMebiBytes(16).Bytes;

    // estimates total uncompressed bytes across all files and directories
    public static long ComputeTotalBytes(List<string> paths)
    {
        long total = 0;
        foreach (var path in paths)
        {
            if (Directory.Exists(path))
            {
                foreach (var file in Directory.EnumerateFiles(path, "*", SearchOption.AllDirectories))
                {
                    try { total += new FileInfo(file).Length; }
                    catch (IOException) { /* skip inaccessible */ }
                    catch (UnauthorizedAccessException) { /* skip inaccessible */ }
                }
            }
            else if (File.Exists(path))
            {
                try { total += new FileInfo(path).Length; }
                catch (IOException) { /* skip inaccessible */ }
                catch (UnauthorizedAccessException) { /* skip inaccessible */ }
            }
        }
        return total;
    }

    // creates a tar.gz stream from paths, calls onChunk for each 16 MiB chunk of compressed output.
    // onChunk receives (compressedData, sequenceNumber, uncompressedBytesWrittenSoFar).
    // returns SHA-256 hash of all compressed bytes (same data the receiver will hash).
    public static async Task<byte[]> StreamAsync(List<string> paths, Func<byte[], int, long, Task> onChunk, CancellationToken cancel)
    {
        var sha = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        try
        {
            var sequence = 0;
            long uncompressedWritten = 0;

            await using var chunker = new ChunkingWriteStream(ChunkSize, async data =>
            {
                // ReSharper disable once AccessToDisposedClosure
                sha.AppendData(data);
                // ReSharper disable once AccessToModifiedClosure
                await onChunk(data, sequence++, uncompressedWritten);
            });

            await using (var gzip = new GZipStream(chunker, CompressionLevel.Optimal, leaveOpen: true))
            {
                await using var tar = new TarWriter(gzip, TarEntryFormat.Gnu, leaveOpen: true);
                foreach (var path in paths)
                {
                    cancel.ThrowIfCancellationRequested();
                    if (Directory.Exists(path))
                        uncompressedWritten += await AddDirectoryAsync(tar, path, cancel);
                    else if (File.Exists(path))
                        uncompressedWritten += await AddFileAsync(tar, path, Path.GetFileName(path), cancel);
                }
                // dispose tar first to flush trailing blocks into gzip
            }
            // gzip is now disposed — all compressed bytes have been written to chunker

            // flush any remaining partial chunk
            await chunker.FlushFinalAsync();

            return sha.GetHashAndReset();
        }
        finally
        {
            sha.Dispose();
        }
    }

    private static async Task<long> AddDirectoryAsync(TarWriter tar, string dirPath, CancellationToken cancel)
    {
        var parent = Path.GetDirectoryName(dirPath) ?? dirPath;
        long bytes = 0;

        // add the root dir entry itself so empty folders are preserved in the archive
        var rootEntry = Path.GetRelativePath(parent, dirPath).Replace('\\', '/') + "/";
        await tar.WriteEntryAsync(new GnuTarEntry(TarEntryType.Directory, rootEntry), cancel);

        // enumerate all subdirectories so empty ones get explicit entries in the archive
        foreach (var dir in Directory.EnumerateDirectories(dirPath, "*", SearchOption.AllDirectories))
        {
            cancel.ThrowIfCancellationRequested();
            var entryName = Path.GetRelativePath(parent, dir).Replace('\\', '/') + "/";
            await tar.WriteEntryAsync(new GnuTarEntry(TarEntryType.Directory, entryName), cancel);
        }

        foreach (var file in Directory.EnumerateFiles(dirPath, "*", SearchOption.AllDirectories))
        {
            cancel.ThrowIfCancellationRequested();
            var entryName = Path.GetRelativePath(parent, file).Replace('\\', '/');
            bytes += await AddFileAsync(tar, file, entryName, cancel);
        }
        return bytes;
    }

    private static async Task<long> AddFileAsync(TarWriter tar, string filePath, string entryName, CancellationToken cancel)
    {
        try
        {
            var size = new FileInfo(filePath).Length;
            await tar.WriteEntryAsync(filePath, entryName, cancel);
            return size;
        }
        catch (IOException) { return 0; /* skip inaccessible */ }
        catch (UnauthorizedAccessException) { return 0; /* skip inaccessible */ }
    }

    // write-only stream that accumulates bytes and fires a callback for each full chunk
    internal sealed class ChunkingWriteStream(int chunkSize, Func<byte[], Task> onChunk) : Stream
    {
        private byte[] _buffer = new byte[chunkSize];
        private int _pos;

        public override bool CanRead => false;
        public override bool CanSeek => false;
        public override bool CanWrite => true;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override void Flush() { }
        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();

        public override void Write(byte[] buffer, int offset, int count) =>
            WriteMemoryAsync(buffer.AsMemory(offset, count)).AsTask().GetAwaiter().GetResult();

        public override ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default) =>
            WriteMemoryAsync(buffer);

        public override Task WriteAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) =>
            WriteMemoryAsync(buffer.AsMemory(offset, count)).AsTask();

        private async ValueTask WriteMemoryAsync(ReadOnlyMemory<byte> buffer)
        {
            var remaining = buffer.Length;
            var srcOffset = 0;
            while (remaining > 0)
            {
                var canFit = chunkSize - _pos;
                var toCopy = Math.Min(canFit, remaining);
                buffer.Slice(srcOffset, toCopy).CopyTo(_buffer.AsMemory(_pos));
                _pos += toCopy;
                srcOffset += toCopy;
                remaining -= toCopy;
                if (_pos == chunkSize)
                    await FlushChunkAsync();
            }
        }

        // flush any remaining bytes as a final (possibly partial) chunk
        public async Task FlushFinalAsync()
        {
            if (_pos > 0)
            {
                var chunk = new byte[_pos];
                Array.Copy(_buffer, chunk, _pos);
                _pos = 0;
                await onChunk(chunk);
            }
        }

        private async Task FlushChunkAsync()
        {
            var chunk = _buffer;
            _buffer = new byte[chunkSize];
            _pos = 0;
            await onChunk(chunk);
        }
    }
}

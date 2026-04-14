using System.Formats.Tar;
using System.IO.Compression;
using System.IO.Pipelines;
using System.Security.Cryptography;
using Cathedral.Exceptions;
using Cathedral.Utils;

namespace Hydra.FileTransfer;

// receives tar.gz chunks via WriteChunkAsync, extracts to a temp directory on the fly.
// call CompleteAsync after the last chunk to flush and finish extraction.
public sealed class TarGzExtractor : IDisposable
{
    private const int ExtractTaskDisposalWaitMs = 2000; // how long to wait for extract loop to exit before disposing _sha

    private readonly string _tempDir;
    private readonly Pipe _pipe = new();
    private readonly Task _extractTask;
    private readonly IncrementalHash _sha = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
    private long _bytesReceived;
    private long _bytesExtracted;
    private volatile byte[]? _cachedHash;
    private readonly Toggle _disposed = new();

    public long BytesReceived => Interlocked.Read(ref _bytesReceived);

    // uncompressed bytes written to disk — use this for progress display
    public long BytesExtracted => Interlocked.Read(ref _bytesExtracted);

    public TarGzExtractor(string tempDir, CancellationToken cancel)
    {
        _tempDir = tempDir;
        Directory.CreateDirectory(tempDir);
        _extractTask = ExtractLoopAsync(cancel);
    }

    public async ValueTask WriteChunkAsync(byte[] data)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        lock (_sha) _sha.AppendData(data);
        Interlocked.Add(ref _bytesReceived, data.Length);
        await _pipe.Writer.WriteAsync(data);
    }

    // call after the last chunk; waits for extraction to finish
    public async ValueTask CompleteAsync()
    {
        await _pipe.Writer.CompleteAsync();
        await _extractTask;
    }

    // call this only after CompleteAsync returns
    public byte[] GetHash()
    {
        if (_cachedHash != null) return _cachedHash;
        lock (_sha)
        {
            if (_cachedHash != null) return _cachedHash;
            var hash = _sha.GetHashAndReset();
            _cachedHash = hash;
            return hash;
        }
    }

    private async Task ExtractLoopAsync(CancellationToken cancel)
    {
        try
        {
            await using var gzip = new GZipStream(_pipe.Reader.AsStream(), CompressionMode.Decompress);
            using var tar = new TarReader(gzip, leaveOpen: false);

            TarEntry? entry;
            while ((entry = await tar.GetNextEntryAsync(copyData: false, cancel)) != null)
            {
                cancel.ThrowIfCancellationRequested();
                string destPath;
                try { destPath = SafePath.ResolveForRoot(_tempDir, entry.Name); }
                catch (Exception e) when (e is PathTraversalException or IllegalPathException) { continue; } // skip entry

                switch (entry.EntryType)
                {
                    case TarEntryType.Directory:
                        Directory.CreateDirectory(destPath);
                        break;
                    case TarEntryType.RegularFile:
                    case TarEntryType.V7RegularFile:
                        Directory.CreateDirectory(Path.GetDirectoryName(destPath)!);
                        await entry.ExtractToFileAsync(destPath, overwrite: true, cancel);
                        Interlocked.Add(ref _bytesExtracted, entry.Length);
                        break;
                    case TarEntryType.SymbolicLink:
                    case TarEntryType.HardLink:
                        break; // skip symlinks and hard links silently
                        // other entry types (device nodes, fifos, etc.) silently skipped
                }
            }
        }
        finally
        {
            await _pipe.Reader.CompleteAsync();
        }
    }

    public void Dispose()
    {
        if (!_disposed.TrySet()) return;
        // complete both ends to unblock ExtractLoopAsync, then wait briefly for it to exit before disposing _sha
        try { _pipe.Writer.Complete(); } catch { /* already completed */ }
        try { _pipe.Reader.Complete(); } catch { /* already completed */ }
        try { _extractTask.Wait(TimeSpan.FromMilliseconds(ExtractTaskDisposalWaitMs)); } catch { /* task faulted or timed out — ignore during disposal */ }
        lock (_sha) _sha.Dispose();
    }
}

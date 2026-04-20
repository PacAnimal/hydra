using System.Formats.Tar;
using System.IO.Compression;
using Hydra.FileTransfer;

namespace Tests.FileTransfer;

[TestFixture]
public class TarGzStreamerTests
{
    private static readonly Action<string> NoFileStart = _ => { };
    private string _tempRoot = null!;

    [SetUp]
    public void SetUp()
    {
        _tempRoot = Path.Combine(Path.GetTempPath(), "hydra-test-" + Guid.NewGuid());
        Directory.CreateDirectory(_tempRoot);
    }

    [TearDown]
    public void TearDown()
    {
        try { Directory.Delete(_tempRoot, recursive: true); }
        catch { /* best effort */ }
    }

    [Test]
    public async Task StreamAsync_SingleFile_ProducesChunksAndHash()
    {
        var file = Path.Combine(_tempRoot, "hello.txt");
        await File.WriteAllTextAsync(file, "hello world");

        var chunks = new List<byte[]>();
        var sha = await TarGzStreamer.StreamAsync([file],
            (data, _, _) => { chunks.Add(data); return Task.CompletedTask; },
            NoFileStart, CancellationToken.None);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(chunks, Is.Not.Empty);
            Assert.That(sha, Has.Length.EqualTo(32));
        }
    }

    [Test]
    public async Task StreamAsync_Directory_IncludesNestedFiles()
    {
        var dir = Path.Combine(_tempRoot, "mydir");
        Directory.CreateDirectory(Path.Combine(dir, "sub"));
        await File.WriteAllTextAsync(Path.Combine(dir, "a.txt"), "aaa");
        await File.WriteAllTextAsync(Path.Combine(dir, "sub", "b.txt"), "bbb");

        var chunks = new List<byte[]>();
        await TarGzStreamer.StreamAsync([dir],
            (data, _, _) => { chunks.Add(data); return Task.CompletedTask; },
            NoFileStart, CancellationToken.None);

        // decompress and verify both files are in the archive
        var combined = Combine(chunks);
        var entries = await ExtractEntryNames(combined);
        Assert.That(entries, Does.Contain("mydir/a.txt"));
        Assert.That(entries, Does.Contain("mydir/sub/b.txt"));
    }

    [Test]
    public async Task StreamAsync_EmptyDirectory_ProducesValidArchive()
    {
        var dir = Path.Combine(_tempRoot, "empty");
        Directory.CreateDirectory(dir);

        var sha = await TarGzStreamer.StreamAsync([dir],
            (_, _, _) => Task.CompletedTask,
            NoFileStart, CancellationToken.None);

        // valid archive with a hash, even if empty
        Assert.That(sha, Has.Length.EqualTo(32));
    }

    [Test]
    public async Task StreamAsync_MissingFile_SkippedGracefully()
    {
        var real = Path.Combine(_tempRoot, "real.txt");
        await File.WriteAllTextAsync(real, "data");
        var missing = Path.Combine(_tempRoot, "nonexistent.txt");

        var chunks = new List<byte[]>();
        await TarGzStreamer.StreamAsync([real, missing],
            (data, _, _) => { chunks.Add(data); return Task.CompletedTask; },
            NoFileStart, CancellationToken.None);

        var entries = await ExtractEntryNames(Combine(chunks));
        using (Assert.EnterMultipleScope())
        {
            Assert.That(entries, Does.Contain("real.txt"));
            Assert.That(entries, Has.Count.EqualTo(1));
        }
    }

    [Test]
    public async Task StreamAsync_Cancellation_ThrowsOperationCancelled()
    {
        var file = Path.Combine(_tempRoot, "big.txt");
        await File.WriteAllTextAsync(file, new string('x', 100_000));

        using var cts = new CancellationTokenSource();
        cts.Cancel();

        Assert.ThrowsAsync<OperationCanceledException>(async () =>
            await TarGzStreamer.StreamAsync([file],
                (_, _, _) => Task.CompletedTask,
                NoFileStart, cts.Token));
    }

    [Test]
    public void ComputeTotalBytes_MatchesActualFileSize()
    {
        var file = Path.Combine(_tempRoot, "sized.txt");
        File.WriteAllText(file, new string('a', 1234));

        Assert.That(TarGzStreamer.ComputeTotalBytes([file]), Is.EqualTo(1234));
    }

    [Test]
    public void ComputeTotalBytes_DirectoryRecurses()
    {
        var dir = Path.Combine(_tempRoot, "dir");
        Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(dir, "a.txt"), "aaa");
        File.WriteAllText(Path.Combine(dir, "b.txt"), "bb");

        Assert.That(TarGzStreamer.ComputeTotalBytes([dir]), Is.EqualTo(5));
    }

    [Test]
    public void ComputeTotalBytes_MissingFile_ReturnsZero()
    {
        Assert.That(TarGzStreamer.ComputeTotalBytes(["/nonexistent/file.txt"]), Is.Zero);
    }

    [Test]
    public async Task StreamAsync_SequenceNumbersAreMonotonic()
    {
        var file = Path.Combine(_tempRoot, "seq.txt");
        await File.WriteAllTextAsync(file, "data");

        var sequences = new List<int>();
        await TarGzStreamer.StreamAsync([file],
            (_, seq, _) => { sequences.Add(seq); return Task.CompletedTask; },
            NoFileStart, CancellationToken.None);

        for (var i = 0; i < sequences.Count; i++)
            Assert.That(sequences[i], Is.EqualTo(i));
    }

    private static byte[] Combine(List<byte[]> chunks) => [.. chunks.SelectMany(c => c)];

    private static async Task<List<string>> ExtractEntryNames(byte[] compressed)
    {
        var names = new List<string>();
        await using var ms = new MemoryStream(compressed);
        await using var gzip = new GZipStream(ms, CompressionMode.Decompress);
        using var tar = new TarReader(gzip);
        TarEntry? entry;
        while ((entry = await tar.GetNextEntryAsync()) != null)
            names.Add(entry.Name);
        return names;
    }
}

[TestFixture]
public class TarGzExtractorTests
{
    private static readonly Action<string> NoFileStart = _ => { };
    private string _tempRoot = null!;

    [SetUp]
    public void SetUp()
    {
        _tempRoot = Path.Combine(Path.GetTempPath(), "hydra-test-" + Guid.NewGuid());
        Directory.CreateDirectory(_tempRoot);
    }

    [TearDown]
    public void TearDown()
    {
        try { Directory.Delete(_tempRoot, recursive: true); }
        catch { /* best effort */ }
    }

    [Test]
    public async Task RoundTrip_SingleFile_ExtractsCorrectly()
    {
        var srcFile = Path.Combine(_tempRoot, "hello.txt");
        await File.WriteAllTextAsync(srcFile, "hello world");

        var (chunks, expectedHash) = await StreamToChunks([srcFile]);

        var extractDir = Path.Combine(_tempRoot, "extract");
        using var extractor = new TarGzExtractor(extractDir, NoFileStart, CancellationToken.None);
        foreach (var chunk in chunks)
            await extractor.WriteChunkAsync(chunk);
        await extractor.CompleteAsync();

        var actualHash = extractor.GetHash();
        Assert.That(actualHash, Is.EqualTo(expectedHash));

        var extracted = Path.Combine(extractDir, "hello.txt");
        using (Assert.EnterMultipleScope())
        {
            Assert.That(File.Exists(extracted), Is.True);
            Assert.That(await File.ReadAllTextAsync(extracted), Is.EqualTo("hello world"));
        }
    }

    [Test]
    public async Task RoundTrip_DirectoryTree_ExtractsAll()
    {
        var dir = Path.Combine(_tempRoot, "project");
        Directory.CreateDirectory(Path.Combine(dir, "sub"));
        await File.WriteAllTextAsync(Path.Combine(dir, "root.txt"), "root");
        await File.WriteAllTextAsync(Path.Combine(dir, "sub", "nested.txt"), "nested");

        var (chunks, expectedHash) = await StreamToChunks([dir]);

        var extractDir = Path.Combine(_tempRoot, "extract");
        using var extractor = new TarGzExtractor(extractDir, NoFileStart, CancellationToken.None);
        foreach (var chunk in chunks)
            await extractor.WriteChunkAsync(chunk);
        await extractor.CompleteAsync();

        using (Assert.EnterMultipleScope())
        {
            Assert.That(extractor.GetHash(), Is.EqualTo(expectedHash));
            Assert.That(await File.ReadAllTextAsync(Path.Combine(extractDir, "project", "root.txt")), Is.EqualTo("root"));
            Assert.That(await File.ReadAllTextAsync(Path.Combine(extractDir, "project", "sub", "nested.txt")), Is.EqualTo("nested"));
        }
    }

    [Test]
    public async Task RoundTrip_HashMismatch_DetectedByReceiver()
    {
        var file = Path.Combine(_tempRoot, "data.txt");
        await File.WriteAllTextAsync(file, "some data");

        var (chunks, _) = await StreamToChunks([file]);

        var extractDir = Path.Combine(_tempRoot, "extract");
        using var extractor = new TarGzExtractor(extractDir, NoFileStart, CancellationToken.None);
        foreach (var chunk in chunks)
            await extractor.WriteChunkAsync(chunk);
        await extractor.CompleteAsync();

        var actualHash = extractor.GetHash();
        var fakeHash = new byte[32]; // all zeros
        Assert.That(actualHash.SequenceEqual(fakeHash), Is.False);
    }

    [Test]
    public async Task WriteChunkAsync_TracksBytesReceived()
    {
        var file = Path.Combine(_tempRoot, "tracked.txt");
        await File.WriteAllTextAsync(file, new string('x', 500));

        var (chunks, _) = await StreamToChunks([file]);

        var extractDir = Path.Combine(_tempRoot, "extract");
        using var extractor = new TarGzExtractor(extractDir, NoFileStart, CancellationToken.None);
        foreach (var chunk in chunks)
            await extractor.WriteChunkAsync(chunk);

        Assert.That(extractor.BytesReceived, Is.EqualTo(chunks.Sum(c => (long)c.Length)));
    }

    [Test]
    public async Task PathTraversal_EntrySkipped()
    {
        // craft a tar.gz with a path traversal entry (../../etc/passwd)
        var archiveBytes = await CreateArchiveWithEntry("../../etc/evil.txt", "malicious");

        var extractDir = Path.Combine(_tempRoot, "extract");
        using var extractor = new TarGzExtractor(extractDir, NoFileStart, CancellationToken.None);
        await extractor.WriteChunkAsync(archiveBytes);
        await extractor.CompleteAsync();

        using (Assert.EnterMultipleScope())
        {
            // the evil file should not exist outside the extract dir
            Assert.That(File.Exists(Path.Combine(_tempRoot, "evil.txt")), Is.False);
            Assert.That(File.Exists(Path.Combine(extractDir, "evil.txt")), Is.False);
        }
    }

    [Test]
    public async Task AbsolutePath_NormalizedIntoExtractDir()
    {
        // absolute paths like /etc/evil.txt should be treated as relative and rooted inside the extract dir,
        // not written to the absolute filesystem path
        var archiveBytes = await CreateArchiveWithEntry("/etc/evil.txt", "malicious");

        var extractDir = Path.Combine(_tempRoot, "extract");
        using var extractor = new TarGzExtractor(extractDir, NoFileStart, CancellationToken.None);
        await extractor.WriteChunkAsync(archiveBytes);
        await extractor.CompleteAsync();

        using (Assert.EnterMultipleScope())
        {
            // file must land inside the extract dir, not at the real /etc/evil.txt
            Assert.That(File.Exists("/etc/evil.txt"), Is.False);
            Assert.That(File.Exists(Path.Combine(extractDir, "etc", "evil.txt")), Is.True);
        }
    }

    [Test]
    public async Task SymlinkEntry_Skipped()
    {
        // craft a tar.gz with a symbolic link entry
        var archiveBytes = await CreateSymlinkArchive("link.txt", "/etc/passwd");

        var extractDir = Path.Combine(_tempRoot, "extract");
        using var extractor = new TarGzExtractor(extractDir, NoFileStart, CancellationToken.None);
        await extractor.WriteChunkAsync(archiveBytes);
        await extractor.CompleteAsync();

        // symlink should not have been created
        Assert.That(Directory.GetFileSystemEntries(extractDir), Is.Empty);
    }

    [Test]
    public async Task Dispose_AfterWriteBeforeComplete_DoesNotThrow()
    {
        var file = Path.Combine(_tempRoot, "abort.txt");
        await File.WriteAllTextAsync(file, "data");

        var (chunks, _) = await StreamToChunks([file]);

        var extractDir = Path.Combine(_tempRoot, "extract");
        var extractor = new TarGzExtractor(extractDir, NoFileStart, CancellationToken.None);
        foreach (var chunk in chunks)
            await extractor.WriteChunkAsync(chunk);

        // dispose without calling CompleteAsync — should not throw
        Assert.DoesNotThrow(() => extractor.Dispose());
    }

    [Test]
    public void MoveTo_ConflictingFile_AutoRenames()
    {
        var src = Path.Combine(_tempRoot, "src");
        var dest = Path.Combine(_tempRoot, "dest");
        Directory.CreateDirectory(src);
        Directory.CreateDirectory(dest);

        File.WriteAllText(Path.Combine(src, "file.txt"), "new");
        File.WriteAllText(Path.Combine(dest, "file.txt"), "existing");

        FileUtils.MoveTo(src, dest);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(File.ReadAllText(Path.Combine(dest, "file.txt")), Is.EqualTo("existing"));
            Assert.That(File.ReadAllText(Path.Combine(dest, "file (1).txt")), Is.EqualTo("new"));
        }
    }

    [Test]
    public void MoveTo_ConflictingDirectory_AutoRenames()
    {
        var src = Path.Combine(_tempRoot, "src");
        var dest = Path.Combine(_tempRoot, "dest");
        Directory.CreateDirectory(Path.Combine(src, "mydir"));
        Directory.CreateDirectory(Path.Combine(dest, "mydir"));

        File.WriteAllText(Path.Combine(src, "mydir", "new.txt"), "new");
        File.WriteAllText(Path.Combine(dest, "mydir", "old.txt"), "old");

        FileUtils.MoveTo(src, dest);

        using (Assert.EnterMultipleScope())
        {
            // original dir untouched
            Assert.That(File.ReadAllText(Path.Combine(dest, "mydir", "old.txt")), Is.EqualTo("old"));
            // moved dir renamed
            Assert.That(File.ReadAllText(Path.Combine(dest, "mydir (1)", "new.txt")), Is.EqualTo("new"));
        }
    }

    [Test]
    public void MoveTo_NoConflict_MovesDirectly()
    {
        var src = Path.Combine(_tempRoot, "src");
        var dest = Path.Combine(_tempRoot, "dest");
        Directory.CreateDirectory(src);
        Directory.CreateDirectory(dest);

        File.WriteAllText(Path.Combine(src, "unique.txt"), "data");

        FileUtils.MoveTo(src, dest);

        Assert.That(File.ReadAllText(Path.Combine(dest, "unique.txt")), Is.EqualTo("data"));
    }

    [Test]
    public async Task RoundTrip_EmptyFile_ExtractsCorrectly()
    {
        var file = Path.Combine(_tempRoot, "empty.txt");
        await File.WriteAllBytesAsync(file, []);

        var (chunks, expectedHash) = await StreamToChunks([file]);

        var extractDir = Path.Combine(_tempRoot, "extract");
        using var extractor = new TarGzExtractor(extractDir, NoFileStart, CancellationToken.None);
        foreach (var chunk in chunks)
            await extractor.WriteChunkAsync(chunk);
        await extractor.CompleteAsync();

        using (Assert.EnterMultipleScope())
        {
            Assert.That(extractor.GetHash(), Is.EqualTo(expectedHash));
            Assert.That(File.Exists(Path.Combine(extractDir, "empty.txt")), Is.True);
            Assert.That(new FileInfo(Path.Combine(extractDir, "empty.txt")).Length, Is.Zero);
        }
    }

    [Test]
    public async Task RoundTrip_SpecialCharactersInFilename_ExtractsCorrectly()
    {
        var file = Path.Combine(_tempRoot, "héllo wörld (1).txt");
        await File.WriteAllTextAsync(file, "unicode");

        var (chunks, expectedHash) = await StreamToChunks([file]);

        var extractDir = Path.Combine(_tempRoot, "extract");
        using var extractor = new TarGzExtractor(extractDir, NoFileStart, CancellationToken.None);
        foreach (var chunk in chunks)
            await extractor.WriteChunkAsync(chunk);
        await extractor.CompleteAsync();

        using (Assert.EnterMultipleScope())
        {
            Assert.That(extractor.GetHash(), Is.EqualTo(expectedHash));
            Assert.That(File.Exists(Path.Combine(extractDir, "héllo wörld (1).txt")), Is.True);
        }
    }

    [Test]
    public async Task CorruptData_ExtractThrowsOrCompletes()
    {
        // feeding random bytes should either throw or complete without crashing — must not hang
        var garbage = new byte[1024];
        new Random(42).NextBytes(garbage);

        var extractDir = Path.Combine(_tempRoot, "extract");
        using var extractor = new TarGzExtractor(extractDir, NoFileStart, CancellationToken.None);
        await extractor.WriteChunkAsync(garbage);
        // completing with corrupt data should throw (invalid gzip/tar) without hanging
        Assert.CatchAsync<Exception>(async () => await extractor.CompleteAsync());
    }

    [Test]
    public async Task TruncatedData_ExtractThrowsOrCompletes()
    {
        var file = Path.Combine(_tempRoot, "data.txt");
        await File.WriteAllTextAsync(file, new string('x', 10_000));

        var (chunks, _) = await StreamToChunks([file]);
        // send only the first half of chunks to simulate a truncated stream
        var half = chunks.Take(chunks.Count / 2).ToList();

        var extractDir = Path.Combine(_tempRoot, "extract");
        using var extractor = new TarGzExtractor(extractDir, NoFileStart, CancellationToken.None);
        foreach (var chunk in half)
            await extractor.WriteChunkAsync(chunk);
        Assert.CatchAsync<Exception>(async () => await extractor.CompleteAsync());
    }

    // -- helpers --

    private static async Task<(List<byte[]> chunks, byte[] hash)> StreamToChunks(List<string> paths)
    {
        var chunks = new List<byte[]>();
        var hash = await TarGzStreamer.StreamAsync(paths,
            (data, _, _) => { chunks.Add(data); return Task.CompletedTask; },
            NoFileStart, CancellationToken.None);
        return (chunks, hash);
    }

    private static async Task<byte[]> CreateArchiveWithEntry(string entryName, string content)
    {
        await using var ms = new MemoryStream();
        await using (var gzip = new GZipStream(ms, CompressionLevel.Optimal, leaveOpen: true))
        {
            await using var tar = new TarWriter(gzip, TarEntryFormat.Gnu, leaveOpen: true);
            var data = System.Text.Encoding.UTF8.GetBytes(content);
            var entry = new GnuTarEntry(TarEntryType.RegularFile, entryName)
            {
                DataStream = new MemoryStream(data),
            };
            await tar.WriteEntryAsync(entry);
        }
        return ms.ToArray();
    }

    private static async Task<byte[]> CreateSymlinkArchive(string linkName, string target)
    {
        await using var ms = new MemoryStream();
        await using (var gzip = new GZipStream(ms, CompressionLevel.Optimal, leaveOpen: true))
        {
            await using var tar = new TarWriter(gzip, TarEntryFormat.Gnu, leaveOpen: true);
            var entry = new GnuTarEntry(TarEntryType.SymbolicLink, linkName)
            {
                LinkName = target,
            };
            await tar.WriteEntryAsync(entry);
        }
        return ms.ToArray();
    }
}

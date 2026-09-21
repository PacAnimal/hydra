using Hydra.Management;

namespace Tests.Management;

public class TransactionalConfigStoreTests
{
    private string _directory = null!;
    private string _path = null!;

    [SetUp]
    public void SetUp()
    {
        _directory = Path.Combine(TestContext.CurrentContext.WorkDirectory, "management-config", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_directory);
        _path = Path.Combine(_directory, "hydra.conf");
        File.WriteAllText(_path, Valid("Home"));
    }

    [TearDown]
    public void TearDown() => Directory.Delete(_directory, true);

    [Test]
    public async Task Save_ValidatesAndUpdatesRevision()
    {
        var store = new TransactionalConfigStore(new HydraRuntimeInfo(_path, DateTimeOffset.UtcNow));
        var before = await store.ReadAsync();

        var after = await store.SaveAsync(before.Revision, Valid("Work"), CancellationToken.None);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(after.Revision, Is.Not.EqualTo(before.Revision));
            Assert.That(File.ReadAllText(_path), Does.Contain("Work"));
            Assert.That(TransactionalConfigStore.Validate(after.Json).Valid, Is.True);
        }
    }

    [Test]
    public async Task Save_PreservesUnixFileMode()
    {
        if (OperatingSystem.IsWindows()) Assert.Ignore("Unix permission test");
        const UnixFileMode expected = UnixFileMode.UserRead | UnixFileMode.UserWrite;
#pragma warning disable CA1416
        File.SetUnixFileMode(_path, expected);
        var store = new TransactionalConfigStore(new HydraRuntimeInfo(_path, DateTimeOffset.UtcNow));
        var before = await store.ReadAsync();

        await store.SaveAsync(before.Revision, Valid("Work"), CancellationToken.None);

        Assert.That(File.GetUnixFileMode(_path), Is.EqualTo(expected));
#pragma warning restore CA1416
    }

    [Test]
    public async Task Save_RejectsConcurrentEditWithoutOverwriting()
    {
        var store = new TransactionalConfigStore(new HydraRuntimeInfo(_path, DateTimeOffset.UtcNow));
        var before = await store.ReadAsync();
        File.WriteAllText(_path, Valid("External"));

        Assert.That(async () => await store.SaveAsync(before.Revision, Valid("TUI"), CancellationToken.None),
            Throws.InvalidOperationException.With.Message.Contains("changed outside"));
        Assert.That(File.ReadAllText(_path), Does.Contain("External"));
    }

    [Test]
    public async Task Save_RejectsInvalidJsonWithoutOverwriting()
    {
        var store = new TransactionalConfigStore(new HydraRuntimeInfo(_path, DateTimeOffset.UtcNow));
        var before = await store.ReadAsync();

        Assert.That(async () => await store.SaveAsync(before.Revision, "{ broken", CancellationToken.None),
            Throws.InvalidOperationException);
        Assert.That(File.ReadAllText(_path), Is.EqualTo(before.Json));
    }

    private static string Valid(string profile) => $$"""
        {
          "name": "test-host",
          "profiles": [{
            "profileName": "{{profile}}",
            "mode": "Slave",
            "embeddedStyx": { "server": "http://127.0.0.1:5000", "password": "test" }
          }]
        }
        """;
    /// <summary>
    /// A reader in another store instance does not cost the saver its save.
    ///
    /// <para><b>The TUI is a separate process from the service, and both touch hydra.conf.</b> A reader
    /// opens it without <c>FILE_SHARE_DELETE</c>, so on Windows a save landing at that instant is REFUSED
    /// and the user's config edit is discarded — while POSIX hides it completely by letting the rename
    /// through and leaving the reader on the old inode. The same defect was found and proved in the
    /// remote-management store; this is the same shape over a file that matters more.</para>
    ///
    /// <para>Each save is asserted to succeed AND the final content checked, because "it did not throw" and
    /// "the edit is on disk" are two claims and only one of them breaks first.</para>
    /// </summary>
    [Test]
    public async Task AConcurrentReaderDoesNotCostASaveItsWrite()
    {
        var runtime = new HydraRuntimeInfo(_path, DateTimeOffset.UtcNow);
        var writer = new TransactionalConfigStore(runtime);
        var reader = new TransactionalConfigStore(runtime);

        var reading = new CancellationTokenSource();
        // The TOKEN is captured, not the source, so the loop cannot touch a disposed one however the awaits
        // below unwind.
        var token = reading.Token;
        var reads = Task.Run(async () =>
        {
            while (!token.IsCancellationRequested)
                await reader.ReadAsync(CancellationToken.None);
        }, CancellationToken.None);

        var document = await writer.ReadAsync();
        for (var i = 0; i < 15; i++)
            document = await writer.SaveAsync(document.Revision, Valid($"Desk{i}"), CancellationToken.None);

        await reading.CancelAsync();
        await reads;
        reading.Dispose();

        Assert.That(await File.ReadAllTextAsync(_path), Does.Contain("Desk14"),
            "a save was lost while another instance was reading — on Windows the reader's handle refuses the replace");
    }

    /// <summary>
    /// Two holders of the config lock never overlap, which is what every claim above rests on.
    ///
    /// <para>Without this the concurrency tests could pass because the lock does nothing and the timing
    /// happened to be kind. A counter that ever sees two holders at once fails it.</para>
    /// </summary>
    [Test]
    public async Task TheConfigLockAdmitsOneHolderAtATime()
    {
        var lockPath = ConfigFileLock.ConfigPathFor(_path);
        var inside = 0;
        var overlapped = false;

        await Task.WhenAll(Enumerable.Range(0, 8).Select(_ => Task.Run(async () =>
        {
            for (var i = 0; i < 10; i++)
            {
                await using var held = await ConfigFileLock.Acquire(lockPath, CancellationToken.None);
                if (Interlocked.Increment(ref inside) != 1) overlapped = true;
                await Task.Yield();
                Interlocked.Decrement(ref inside);
            }
        })));

        Assert.That(overlapped, Is.False, "two callers held the config lock at once, so it is not excluding anything");
    }
    /// <summary>
    /// Concurrent read-modify-write loses nothing — the test that fails on EVERY platform when the config
    /// store loses its cross-process lock.
    ///
    /// <para><b>This exists because the other concurrency test here cannot see the defect on POSIX.</b>
    /// Measured: deleting <c>ConfigFileLock</c> from <c>TransactionalConfigStore</c> entirely left all 75
    /// management tests green, so two of the three lanes said nothing about the change that matters most in
    /// this file. The Windows symptom is a refused replace; the portable one is a LOST UPDATE, and that is
    /// what this catches.</para>
    ///
    /// <para>Without a lock spanning the compare and the write, two savers read the same revision, both find
    /// it unchanged, and both write — so one edit disappears with nobody told. With the lock, the loser's
    /// revision check fails, it is told so, and it retries. Every name must therefore survive.</para>
    /// </summary>
    [Test]
    public async Task ConcurrentSavesLoseNoEdit()
    {
        var runtime = new HydraRuntimeInfo(_path, DateTimeOffset.UtcNow);
        const int perWriter = 8;

        static async Task Writer(TransactionalConfigStore store, string tag)
        {
            for (var i = 0; i < perWriter; i++)
            {
                var name = $"{tag}{i}";
                // Retry on a losing revision check — that refusal is the lock WORKING, and the edit is only
                // lost if it never lands at all.
                while (true)
                {
                    var current = await store.ReadAsync();
                    // Appended to the host NAME: an edit that accumulates and stays valid. Adding profiles
                    // does not — only one may be default — and the point here is concurrency, not schema.
                    const string marker = "\"name\": \"";
                    var start = current.Json.IndexOf(marker, StringComparison.Ordinal) + marker.Length;
                    var end = current.Json.IndexOf('"', start);
                    var next = string.Concat(current.Json.AsSpan(0, end), ".", name, current.Json.AsSpan(end));
                    try
                    {
                        await store.SaveAsync(current.Revision, next, CancellationToken.None);
                        break;
                    }
                    catch (InvalidOperationException ex) when (ex.Message.Contains("changed outside"))
                    {
                        // somebody else got there first; re-read and reapply
                    }
                }
            }
        }

        await Task.WhenAll(
            Writer(new TransactionalConfigStore(runtime), "alpha"),
            Writer(new TransactionalConfigStore(runtime), "beta"));

        var final = await File.ReadAllTextAsync(_path);
        var missing = Enumerable.Range(0, perWriter)
            .SelectMany(i => new[] { $"alpha{i}", $"beta{i}" })
            .Where(name => !final.Contains(name))
            .ToList();

        Assert.That(missing, Is.Empty,
            "an edit was written and then silently overwritten — two savers passed the same revision check, which is what the cross-process lock is for");
    }
    /// <summary>
    /// Taking the lock twice on one call stack fails AT ONCE and says what happened.
    ///
    /// <para>It is exclusive and not re-entrant, so a second take waits for itself for the whole budget and
    /// then throws "the process cannot access the file" — five seconds, naming nothing. That has already
    /// happened here once, when rollback called a read that took the lock rollback was holding, and two
    /// more call sites sit one edit away from it.</para>
    /// </summary>
    [Test]
    public async Task TakingTheConfigLockTwiceOnOneStackSaysSoImmediately()
    {
        var lockPath = ConfigFileLock.ConfigPathFor(_path);
        await using var held = await ConfigFileLock.Acquire(lockPath, CancellationToken.None);

        // A short budget, because the point is WHICH error it ends with, not how long it waits for it.
        var again = Assert.ThrowsAsync<InvalidOperationException>(async () =>
            await ConfigFileLock.Acquire(lockPath, TimeSpan.FromMilliseconds(200), CancellationToken.None));

        Assert.That(again!.Message, Does.Contain("not re-entrant"),
            "a second take on the same stack must name the problem, not time out with a message about file sharing");
    }
}

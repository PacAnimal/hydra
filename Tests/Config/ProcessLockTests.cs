using Hydra.Platform;

namespace Tests.Config;

[TestFixture]
public class ProcessLockTests
{
    private string _path = null!;

    [SetUp]
    public void SetUp() => _path = Path.GetTempFileName();

    [TearDown]
    public void TearDown()
    {
        if (File.Exists(_path))
            File.Delete(_path);
    }

    [Test]
    public void Acquire_WritesCurrentPid()
    {
        // PID reading uses raw syscalls on unix; not testable on windows (FileShare.None prevents reads)
        Assume.That(!OperatingSystem.IsWindows());
        using var _ = ProcessLock.Acquire(_path);
        Assert.That(ProcessLock.TryReadPid(_path), Is.EqualTo(Environment.ProcessId));
    }

    [Test]
    public void Acquire_ThrowsWhenAlreadyLocked()
    {
        using var first = ProcessLock.Acquire(_path);
        // maxAttempts:1 → fail fast without the default ~2s retry budget
        Assert.Throws<InvalidOperationException>(() => ProcessLock.Acquire(_path, maxAttempts: 1, retryDelayMs: 0));
    }

    [Test]
    public void Acquire_IncludesPidInError()
    {
        // PID reading uses raw syscalls on unix; not testable on windows
        Assume.That(!OperatingSystem.IsWindows());
        using var first = ProcessLock.Acquire(_path);
        var ex = Assert.Throws<InvalidOperationException>(() => ProcessLock.Acquire(_path, maxAttempts: 1, retryDelayMs: 0))!;
        Assert.That(ex.Message, Does.Contain(Environment.ProcessId.ToString()));
    }

    [Test]
    public void Acquire_IncludesPathInError()
    {
        using var first = ProcessLock.Acquire(_path);
        var ex = Assert.Throws<InvalidOperationException>(() => ProcessLock.Acquire(_path, maxAttempts: 1, retryDelayMs: 0))!;
        Assert.That(ex.Message, Does.Contain(_path));
    }

    [Test]
    public void Acquire_RetriesUntilLockReleased()
    {
        // the dying-parent-vs-restart-child race: the child's Acquire must retry past the moment the
        // parent releases its lock instead of failing on the first attempt. deterministic — the
        // beforeRetry seam releases the held lock, so the next attempt succeeds (no timing).
        var first = ProcessLock.Acquire(_path);
        using var second = ProcessLock.Acquire(_path, maxAttempts: 5, retryDelayMs: 0, beforeRetry: () => first.Dispose());
        Assert.Pass();
    }

    [Test]
    public void Dispose_ReleasesLock()
    {
        var first = ProcessLock.Acquire(_path);
        first.Dispose();
        using var second = ProcessLock.Acquire(_path);
        // PID reading uses raw syscalls on unix; not testable on windows
        if (!OperatingSystem.IsWindows())
            Assert.That(ProcessLock.TryReadPid(_path), Is.EqualTo(Environment.ProcessId));
    }

    [Test]
    public void Dispose_DeletesLockFile()
    {
        var l = ProcessLock.Acquire(_path);
        l.Dispose();
        Assert.That(File.Exists(_path), Is.False);
    }

    [Test]
    public void Dispose_SecondAcquireWorksAfterDispose()
    {
        var first = ProcessLock.Acquire(_path);
        first.Dispose();
        // Acquire uses OpenOrCreate, so it works whether the file was deleted or not
        using var second = ProcessLock.Acquire(_path);
        Assert.Pass();
    }
}

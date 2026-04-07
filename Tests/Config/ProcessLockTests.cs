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
        Assert.Throws<InvalidOperationException>(() => ProcessLock.Acquire(_path));
    }

    [Test]
    public void Acquire_IncludesPidInError()
    {
        // PID reading uses raw syscalls on unix; not testable on windows
        Assume.That(!OperatingSystem.IsWindows());
        using var first = ProcessLock.Acquire(_path);
        var ex = Assert.Throws<InvalidOperationException>(() => ProcessLock.Acquire(_path))!;
        Assert.That(ex.Message, Does.Contain(Environment.ProcessId.ToString()));
    }

    [Test]
    public void Acquire_IncludesPathInError()
    {
        using var first = ProcessLock.Acquire(_path);
        var ex = Assert.Throws<InvalidOperationException>(() => ProcessLock.Acquire(_path))!;
        Assert.That(ex.Message, Does.Contain(_path));
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
}

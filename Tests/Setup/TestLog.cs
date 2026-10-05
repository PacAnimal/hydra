using Cathedral.Logging;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Tests.Setup;

public static class TestLog
{
    internal static readonly string SolutionRoot = FindSolutionRoot(AppContext.BaseDirectory);

    // The Linux container lane writes into this same bind-mounted test-output from its own pid namespace.
    // Both of these are read while LogFilePath initialises, so they are declared above it.
    internal static readonly string Machine = FileSafe(Environment.MachineName);

    // how long another machine's log is presumed live after its last write, since its pid means nothing here
    internal static readonly TimeSpan ForeignLogGrace = TimeSpan.FromMinutes(15);

    public static readonly string LogFilePath = ComputeLogFilePath();

    // One file provider for the whole process, so hosts never append to the same file concurrently, which
    // Windows refuses with "being used by another process".
    private static readonly ServiceProvider FileLogging = StartFileLogging();
    private static readonly ILoggerProvider FileProvider = FileLogging.GetServices<ILoggerProvider>().Single();
    public static readonly ILoggerFactory Factory = FileLogging.GetRequiredService<ILoggerFactory>();

    // how much history test-output keeps. BOTH ceilings are needed: a run count alone lets one noisy run
    // leave hundreds of megabytes behind, and a byte ceiling alone would keep a thousand tiny runs nobody
    // will ever read.
    internal const int KeepRuns = 50;
    private const long KeepBytes = 64L * 1024 * 1024;

    private static string ComputeLogFilePath()
    {
        var outputDir = Path.Combine(SolutionRoot, "test-output");
        Directory.CreateDirectory(outputDir);
        PruneOldRuns(outputDir);
        return Path.Combine(outputDir, LogFileName(DateTimeOffset.UtcNow, Machine, Environment.ProcessId));
    }

    // the machine and pid keep concurrent runs apart, and tell pruning whose log is still being written
    internal static string LogFileName(DateTimeOffset started, string machine, int processId) =>
        $"{started.ToUnixTimeSeconds()}-{machine}-{processId}.log";

    private static string FileSafe(string name) =>
        string.Concat(name.Select(c => char.IsAsciiLetterOrDigit(c) || c is '.' or '-' ? c : '_'));

    // drops the oldest runs until both ceilings hold, never one whose process is still running. best effort
    // on purpose: a log we cannot delete is never a reason to fail the suite that was about to write the next one.
    internal static void PruneOldRuns(string outputDir)
    {
        var runs = new DirectoryInfo(outputDir).GetFiles("*.log")
            .OrderByDescending(f => f.LastWriteTimeUtc)
            .ToArray();

        long kept = 0;
        for (var i = 0; i < runs.Length; i++)
        {
            kept += runs[i].Length;
            if (i == 0 || (i < KeepRuns && kept <= KeepBytes)) continue; // the newest run survives any ceiling
            if (IsStillWriting(runs[i])) continue;
            try { runs[i].Delete(); }
            catch (IOException) { /* still open, or read-only */ }
            catch (UnauthorizedAccessException) { /* not ours to delete */ }
        }
    }

    // a log from this machine is live while its process is; anyone else's pid is from another namespace
    private static bool IsStillWriting(FileInfo log)
    {
        var name = Path.GetFileNameWithoutExtension(log.Name);
        var first = name.IndexOf('-');
        var last = name.LastIndexOf('-');
        if (first > 0 && last > first && name[(first + 1)..last] == Machine && int.TryParse(name[(last + 1)..], out var processId))
            return LiveProcess.IsRunning(processId);
        return DateTime.UtcNow - log.LastWriteTimeUtc < ForeignLogGrace;
    }

    private static string FindSolutionRoot(string startPath)
    {
        var current = new DirectoryInfo(startPath);
        while (current != null)
        {
            // .git is a DIRECTORY in a normal clone and a FILE in a worktree, pointing at the real
            // gitdir. Accepting only the directory made every test in a worktree die in OneTimeSetUp with
            // "Could not find solution root" — which is where a review that wants to mutate the tree safely
            // has to work.
            if (current.GetFiles("*.sln").Length > 0 && (current.GetDirectories(".git").Length > 0 || current.GetFiles(".git").Length > 0))
                return current.FullName;
            current = current.Parent;
        }
        throw new InvalidOperationException($"Could not find solution root starting from {startPath}");
    }

    // nothing hosts this provider, so its flush loop is started here and its buffer drained at exit
    private static ServiceProvider StartFileLogging()
    {
        var services = new ServiceCollection();
        services.AddSereneFileLogging(LogFilePath, fc =>
        {
            fc.TimestampFormat = "yyyy-MM-dd HH:mm:ss.fff";
            fc.TimestampUtc = true;
            fc.MinLogLevel = LogLevel.Debug;
        });
        var provider = services.BuildServiceProvider();
        foreach (var flusher in provider.GetServices<IHostedService>())
            flusher.StartAsync(CancellationToken.None).GetAwaiter().GetResult();
        AppDomain.CurrentDomain.ProcessExit += (_, _) =>
        {
            foreach (var fileProvider in provider.GetServices<ILoggerProvider>())
                fileProvider.Dispose();
        };
        return provider;
    }

    // the Trace rule leaves filtering to the provider's own Debug floor, as AddSereneFileLogging does
    public static void ConfigureFileLogging(IServiceCollection services)
    {
        services.AddLogging(log =>
        {
            log.AddProvider(FileProvider);
            log.Services.Configure<LoggerFilterOptions>(o => o.Rules.Add(new LoggerFilterRule(FileProvider.GetType().FullName, null, LogLevel.Trace, null)));
        });
    }

    public static ILogger<T> CreateLogger<T>() => Factory.CreateLogger<T>();
}

using Cathedral.Logging;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Tests.Setup;

public static class TestLog
{
    internal static readonly string SolutionRoot = FindSolutionRoot(AppContext.BaseDirectory);
    public static readonly string LogFilePath = ComputeLogFilePath();
    public static readonly ILoggerFactory Factory = CreateTestLoggerFactory();

    // how much history test-output keeps. BOTH ceilings are needed: a run count alone lets one noisy run
    // leave hundreds of megabytes behind, and a byte ceiling alone would keep a thousand tiny runs nobody
    // will ever read.
    private const int KeepRuns = 50;
    private const long KeepBytes = 64L * 1024 * 1024;

    private static string ComputeLogFilePath()
    {
        var unixTime = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        var solutionRoot = SolutionRoot;
        var outputDir = Path.Combine(solutionRoot, "test-output");
        Directory.CreateDirectory(outputDir);
        PruneOldRuns(outputDir);
        return Path.Combine(outputDir, $"{unixTime}.log");
    }

    // drops the oldest runs until both ceilings hold. best effort on purpose: a log we cannot delete is
    // never a reason to fail the suite that was about to write the next one.
    private static void PruneOldRuns(string outputDir)
    {
        var runs = new DirectoryInfo(outputDir).GetFiles("*.log")
            .OrderByDescending(f => f.LastWriteTimeUtc)
            .ToArray();

        long kept = 0;
        for (var i = 0; i < runs.Length; i++)
        {
            kept += runs[i].Length;
            if (i == 0 || (i < KeepRuns && kept <= KeepBytes)) continue; // the newest run survives any ceiling
            try { runs[i].Delete(); }
            catch (IOException) { /* still open, or read-only */ }
            catch (UnauthorizedAccessException) { /* not ours to delete */ }
        }
    }

    private static string FindSolutionRoot(string startPath)
    {
        var current = new DirectoryInfo(startPath);
        while (current != null)
        {
            if (current.GetFiles("*.sln").Length > 0 && current.GetDirectories(".git").Length > 0)
                return current.FullName;
            current = current.Parent;
        }
        throw new InvalidOperationException($"Could not find solution root starting from {startPath}");
    }

    private static ILoggerFactory CreateTestLoggerFactory()
    {
        var services = new ServiceCollection();
        ConfigureFileLogging(services);
        return services.BuildServiceProvider().GetRequiredService<ILoggerFactory>();
    }

    public static void ConfigureFileLogging(IServiceCollection services)
    {
        services.AddSereneFileLogging(LogFilePath, fc =>
        {
            fc.TimestampFormat = "yyyy-MM-dd HH:mm:ss.fff";
            fc.TimestampUtc = true;
            fc.MinLogLevel = LogLevel.Debug;
        });
    }

    public static ILogger<T> CreateLogger<T>() => Factory.CreateLogger<T>();
}

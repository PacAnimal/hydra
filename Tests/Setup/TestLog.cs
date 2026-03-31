using Cathedral.Logging;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Tests.Setup;

public static class TestLog
{
    public static readonly string LogFilePath = ComputeLogFilePath();
    public static readonly ILoggerFactory Factory = CreateTestLoggerFactory();

    private static string ComputeLogFilePath()
    {
        var unixTime = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        var solutionRoot = FindSolutionRoot(AppContext.BaseDirectory);
        var outputDir = Path.Combine(solutionRoot, "test-output");
        Directory.CreateDirectory(outputDir);
        return Path.Combine(outputDir, $"{unixTime}.log");
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
            fc.MinLogLevel = LogLevel.Trace;
        });
    }

    public static ILogger<T> CreateLogger<T>() => Factory.CreateLogger<T>();
}

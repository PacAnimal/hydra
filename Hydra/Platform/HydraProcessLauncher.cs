using System.Diagnostics;
using Hydra.Platform.MacOs;

namespace Hydra.Platform;

internal static class HydraProcessLauncher
{
    internal static void Start(string configPath)
    {
        if (OperatingSystem.IsMacOS() && AgentCommands.IsInstalled())
        {
            AgentCommands.Start();
            return;
        }

        var executablePath = Environment.ProcessPath
            ?? throw new InvalidOperationException("cannot determine the Hydra executable path");
        var process = Process.Start(CreateDirectStartInfo(executablePath, configPath))
            ?? throw new InvalidOperationException("failed to start Hydra");
        _ = DrainAsync(process);
    }

    internal static ProcessStartInfo CreateDirectStartInfo(string executablePath, string configPath) => new()
    {
        FileName = executablePath,
        WorkingDirectory = Path.GetDirectoryName(executablePath) ?? Directory.GetCurrentDirectory(),
        UseShellExecute = false,
        CreateNoWindow = true,
        RedirectStandardOutput = true,
        RedirectStandardError = true,
        Environment = { ["CONFIG"] = configPath }
    };

    private static async Task DrainAsync(Process process)
    {
        try
        {
            await Task.WhenAll(process.StandardOutput.ReadToEndAsync(), process.StandardError.ReadToEndAsync());
        }
        catch (ObjectDisposedException) { }
        catch (IOException) { }
        finally { process.Dispose(); }
    }
}

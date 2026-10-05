using System.Diagnostics;
using System.Xml;
using Cathedral.Utils;
using Hydra.Config;
using Hydra.Platform.MacOs;

namespace Hydra.Platform;

internal static class HydraProcessLauncher
{
    internal static void Start(string configPath)
    {
        if (OperatingSystem.IsMacOS() && AgentCommands.IsInstalled())
        {
            EnsureAgentRuns(AgentCommands.InstalledConfigPath(), configPath);
            AgentCommands.Start();
            return;
        }

        var executablePath = Environment.ProcessPath
            ?? throw new InvalidOperationException("cannot determine the Hydra executable path");
        var process = Process.Start(CreateDirectStartInfo(executablePath, configPath))
            ?? throw new InvalidOperationException("failed to start Hydra");
        _ = DrainAsync(process);
    }

    // launchd starts the agent with the config it was installed for, which must be the one asked for
    internal static void EnsureAgentRuns(string agentConfigPath, string configPath)
    {
        if (IsSameFile(agentConfigPath, configPath)) return;
        throw new InvalidOperationException($"The installed Hydra agent runs {agentConfigPath}, not {configPath}. "
            + $"Open the TUI with --config \"{agentConfigPath}\", or reinstall the agent with hydra --install --config \"{configPath}\".");
    }

    /// <summary>
    /// The spelling the TUI should address its daemon by. The management endpoint is named after the config
    /// path as written, so a TUI that reaches the agent's config another way — through <c>/tmp</c> rather than
    /// <c>/private/tmp</c>, or in another case — would poll an endpoint nobody opens.
    /// </summary>
    internal static string AddressedAs(string configPath, string? agentConfigPath) =>
        agentConfigPath != null && IsSameFile(agentConfigPath, configPath) ? agentConfigPath : configPath;

    // the installed agent's spelling of this config, where there is one
    internal static string AddressedAs(string configPath) =>
        AddressedAs(configPath, OperatingSystem.IsMacOS() && AgentCommands.IsInstalled() ? AgentConfigPathOrNull(AgentCommands.InstalledConfigPath) : null);

    // a plist we cannot read or parse leaves the config as given; Start's EnsureAgentRuns reports it
    internal static string? AgentConfigPathOrNull(Func<string> read)
    {
        try
        {
            return read();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or XmlException or InvalidOperationException)
        {
            return null;
        }
    }

    // asks the filesystem, so a linked directory or a case-folding volume cannot make one file look like two
    private static bool IsSameFile(string first, string second)
    {
        if (Identity(first) is { } a && Identity(second) is { } b) return a == b;
        return Path.GetFullPath(first).Equals(Path.GetFullPath(second), StringComparison.Ordinal);
    }

    private static FileIdentity? Identity(string path)
    {
        try
        {
            var target = File.ResolveLinkTarget(path, returnFinalTarget: true)?.FullName ?? path;
            var status = FileSystemUtils.StatWithoutFollowing(target);
            return status is { Exists: true, DeviceId: { } device, FileId: { } file } ? new FileIdentity(device, file) : null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    internal static ProcessStartInfo CreateDirectStartInfo(string executablePath, string configPath) => new()
    {
        FileName = executablePath,
        WorkingDirectory = Path.GetDirectoryName(executablePath) ?? Directory.GetCurrentDirectory(),
        UseShellExecute = false,
        CreateNoWindow = true,
        RedirectStandardOutput = true,
        RedirectStandardError = true,
        Environment = { [HydraArgs.ConfigVariable] = configPath }
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

    private readonly record struct FileIdentity(ulong Device, ulong File);
}

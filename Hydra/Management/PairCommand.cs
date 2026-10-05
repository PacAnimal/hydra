using Hydra.Config;

namespace Hydra.Management;

internal static class PairCommand
{
    // returns the process exit code: 2 for a bad command line, like the daemon and the TUI
    internal static async Task<int> Run(IReadOnlyList<string> args, TextWriter output, TextWriter error)
    {
        HydraArgs? pairArgs;
        try { pairArgs = HydraArgs.Parse(args); }
        catch (ArgumentException ex)
        {
            await error.WriteLineAsync(ex.Message);
            pairArgs = null;
        }
        if (pairArgs is not { Rest.Count: 0 })
        {
            await error.WriteLineAsync(HydraArgs.PairUsage);
            return 2;
        }
        var configPath = HydraConfigFile.ResolvePath(pairArgs.ConfigPath);
        string code;
        try
        {
            code = await new RemoteManagementStore(configPath).CreatePairingCodeAsync();
        }
        catch (UnauthorizedAccessException ex)
        {
            await error.WriteLineAsync($"Hydra's management state beside {configPath} belongs to another account: {ex.Message}");
            await error.WriteLineAsync(RunAsOwner);
            return 1;
        }
        catch (IOException ex)
        {
            await error.WriteLineAsync($"Could not write Hydra's management state beside {configPath}: {ex.Message}");
            await error.WriteLineAsync("If another process still holds that file open, try again once it has let go.");
            return 1;
        }
        await output.WriteLineAsync("Enter this one-time code in the controlling Hydra TUI within 10 minutes:");
        await output.WriteLineAsync(code);
        return 0;
    }

    private static string RunAsOwner => OperatingSystem.IsWindows()
        ? "Run hydra pair from an elevated terminal, or restart the Hydra service and run it as the user signed in at the console."
        : "Run hydra pair as the user Hydra runs as, or with sudo.";
}

using Hydra.Platform;
using Hydra.Platform.MacOs;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Hydra.Management;

internal interface IHydraLifetimeController
{
    void RestartAfterResponse();
    CommandResult ShutdownAfterResponse();
}

internal sealed class HydraLifetimeController(
    IHostApplicationLifetime lifetime,
    ILogger<HydraLifetimeController> log) : IHydraLifetimeController
{
    public void RestartAfterResponse()
    {
        _ = Task.Run(async () =>
        {
            await Task.Delay(350);
            if (OperatingSystem.IsWindows() && RunMode.IsSessionChild)
                lifetime.StopApplication();
            else
                ProcessRestart.Restart();
        });
    }

    public CommandResult ShutdownAfterResponse()
    {
        if (!CanShutdown(OperatingSystem.IsWindows(), RunMode.IsSessionChild))
            return new CommandResult(false,
                "Hydra is managed by the Windows service. Stop it from Windows Services or an elevated terminal.");

        _ = Task.Run(async () =>
        {
            await Task.Delay(350);
            try
            {
                // A KeepAlive LaunchAgent would immediately relaunch Hydra after a normal
                // host shutdown. Unload the user agent first so a TUI shutdown remains stopped.
                if (OperatingSystem.IsMacOS())
                    AgentCommands.Stop();
            }
            catch (Exception ex)
            {
                log.LogError(ex, "Failed to unload the macOS LaunchAgent before shutdown");
            }
            finally
            {
                lifetime.StopApplication();
            }
        });
        return new CommandResult(true, "Hydra shutdown requested.");
    }

    internal static bool CanShutdown(bool isWindows, bool isSessionChild) =>
        !isWindows || !isSessionChild;
}

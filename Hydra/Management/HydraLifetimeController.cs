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
    // long enough for the management response to reach its client before we go
    private static readonly TimeSpan ResponseGrace = TimeSpan.FromMilliseconds(350);

    public void RestartAfterResponse()
    {
        AfterResponse(() =>
        {
            try
            {
                if (OperatingSystem.IsWindows() && RunMode.IsSessionChild)
                    lifetime.StopApplication();
                else
                    ProcessRestart.Restart();
            }
            catch (Exception ex)
            {
                log.LogError(ex, "Failed to restart Hydra after a management request");
            }
        });
    }

    public CommandResult ShutdownAfterResponse()
    {
        if (!CanShutdown(OperatingSystem.IsWindows(), RunMode.IsSessionChild))
            return new CommandResult(false,
                "Hydra is managed by the Windows service. Stop it from Windows Services or an elevated terminal.");

        AfterResponse(() =>
        {
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

    private static void AfterResponse(Action action) => _ = Task.Run(async () =>
    {
        await Task.Delay(ResponseGrace);
        action();
    });

    internal static bool CanShutdown(bool isWindows, bool isSessionChild) =>
        !isWindows || !isSessionChild;
}

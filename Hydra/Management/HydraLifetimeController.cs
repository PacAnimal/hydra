using Hydra.Platform;
using Microsoft.Extensions.Hosting;

namespace Hydra.Management;

internal sealed class HydraLifetimeController(IHostApplicationLifetime lifetime)
{
    internal void RestartAfterResponse()
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
}

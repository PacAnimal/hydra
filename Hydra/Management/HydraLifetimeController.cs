using Hydra.Platform;
using Microsoft.Extensions.Hosting;

namespace Hydra.Management;

internal interface IHydraLifetimeController
{
    void RestartAfterResponse();
}

internal sealed class HydraLifetimeController(IHostApplicationLifetime lifetime) : IHydraLifetimeController
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
}

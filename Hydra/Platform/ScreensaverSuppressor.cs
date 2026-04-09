using Cathedral.Utils;
using Hydra.Config;
using Microsoft.Extensions.Logging;

namespace Hydra.Platform;

public interface IScreensaverSuppressor
{
    void Suppress();
    void Restore();
}

internal sealed class ScreensaverSuppressor(IHydraProfile config, ILogger<ScreensaverSuppressor> log, IScreenSaverSync platform)
    : SimpleHostedService(log, TimeSpan.FromSeconds(5)), IScreensaverSuppressor
{
    private volatile bool _suppressing;

    public void Suppress()
    {
        var was = _suppressing;
        _suppressing = config.SyncScreensaver;
        if (_suppressing && !was)
            log.LogDebug("Screensaver suppression enabled");
        else if (!_suppressing)
            log.LogDebug("Screensaver suppression skipped — SyncScreensaver is disabled");
    }

    public void Restore()
    {
        _suppressing = false;
        platform.Restore();
        log.LogDebug("Screensaver suppression restored");
    }

    protected override Task Execute(CancellationToken cancel)
    {
        if (_suppressing)
        {
            log.LogDebug("Refreshing screensaver suppression");
            platform.Suppress();
        }
        return Task.CompletedTask;
    }

    protected override Task OnShutdown(CancellationToken cancel)
    {
        platform.Restore();
        return Task.CompletedTask;
    }
}

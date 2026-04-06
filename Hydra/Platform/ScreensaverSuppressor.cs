using Cathedral.Utils;
using Hydra.Config;
using Microsoft.Extensions.Logging;

namespace Hydra.Platform;

public interface IScreensaverSuppressor
{
    void Suppress();
    void Restore();
}

internal sealed class ScreensaverSuppressor(HydraConfig config, ILogger<ScreensaverSuppressor> log, IScreenSaverSync platform)
    : SimpleHostedService(log, TimeSpan.FromSeconds(5)), IScreensaverSuppressor
{
    private volatile bool _suppressing;

    public void Suppress() => _suppressing = config.SyncScreensaver;

    public void Restore()
    {
        _suppressing = false;
        platform.Restore();
    }

    protected override Task Execute(CancellationToken cancel)
    {
        if (_suppressing)
            platform.Suppress();
        return Task.CompletedTask;
    }

    protected override Task OnShutdown(CancellationToken cancel)
    {
        platform.Restore();
        return Task.CompletedTask;
    }
}

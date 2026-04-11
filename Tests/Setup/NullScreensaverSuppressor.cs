using Hydra.Platform;

namespace Tests.Setup;

internal sealed class NullScreensaverSuppressor : IScreensaverSuppressor
{
    public void Suppress() { }
    public void Restore() { }
}

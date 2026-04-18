namespace Hydra.Platform;

public interface IOsdNotification
{
    void Show(string message);
}

public sealed class NullOsdNotification : IOsdNotification
{
    public void Show(string message) { }
}

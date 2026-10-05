using Hydra.Platform;

namespace Tests.Setup;

public sealed class RecordingOsd : IOsdNotification
{
    public NotificationQueue<string> Shown { get; } = new();

    public void Show(string message) => Shown.Push(message);
}

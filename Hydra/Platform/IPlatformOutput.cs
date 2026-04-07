using Hydra.Relay;

namespace Hydra.Platform;

public interface IPlatformOutput : IDisposable
{
    void MoveMouse(int x, int y);
    void MoveMouseRelative(int dx, int dy);
    void InjectKey(KeyEventMessage msg);
    void InjectMouseButton(MouseButtonMessage msg);
    void InjectMouseScroll(MouseScrollMessage msg);
}

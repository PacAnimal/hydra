using Hydra.Relay;
using Hydra.Screen;

namespace Hydra.Platform;

public interface IPlatformOutput : IDisposable
{
    List<DetectedScreen> GetAllScreens();
    void MoveMouse(int x, int y);
    void InjectKey(KeyEventMessage msg);
    void InjectMouseButton(MouseButtonMessage msg);
    void InjectMouseScroll(MouseScrollMessage msg);
}

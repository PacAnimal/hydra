using Hydra.Platform;
using Hydra.Relay;

namespace Tests.Setup;

internal sealed class NullPlatformOutput : IPlatformOutput
{
    public void MoveMouse(int x, int y) { }
    public void MoveMouseRelative(int dx, int dy) { }
    public void InjectKey(KeyEventMessage msg) { }
    public void InjectMouseButton(MouseButtonMessage msg) { }
    public void InjectMouseScroll(MouseScrollMessage msg) { }
    public void Dispose() { }
}

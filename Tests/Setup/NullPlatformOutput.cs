using Hydra.Platform;
using Hydra.Relay;

namespace Tests.Setup;

public sealed class NullPlatformOutput : IPlatformOutput
{
    // counted so tests can assert that nothing reached the local machine
    public int MoveCount;
    public int ButtonCount;
    public int ScrollCount;
    public readonly List<KeyEventMessage> Keys = [];

    public int TotalInjections => MoveCount + ButtonCount + ScrollCount + Keys.Count;

    public void MoveMouse(int x, int y) => MoveCount++;
    public void MoveMouseRelative(int dx, int dy) => MoveCount++;
    public void InjectKey(KeyEventMessage msg) => Keys.Add(msg);
    public void InjectMouseButton(MouseButtonMessage msg) => ButtonCount++;
    public void InjectMouseScroll(MouseScrollMessage msg) => ScrollCount++;
    public void Dispose() { }
}

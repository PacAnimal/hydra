using Hydra.Platform;
using Hydra.Relay;

namespace Tests.Setup;

public sealed class NullPlatformOutput : IPlatformOutput
{
    // counted so tests can assert that nothing reached the local machine
    public int MoveCount;
    public int ScrollCount;
    public readonly List<KeyEventMessage> Keys = [];
    public readonly List<MouseButtonMessage> Buttons = [];

    public int TotalInjections => MoveCount + Buttons.Count + ScrollCount + Keys.Count;

    public void MoveMouse(int x, int y) => MoveCount++;
    public void MoveMouseRelative(int dx, int dy) => MoveCount++;
    public void InjectKey(KeyEventMessage msg) => Keys.Add(msg);
    public void InjectMouseButton(MouseButtonMessage msg) => Buttons.Add(msg);
    public void InjectMouseScroll(MouseScrollMessage msg) => ScrollCount++;
    public void RestoreLocks() => LockRestores.Add(Keys.Count);

    // how many keys had been injected each time RestoreLocks ran, so a test sees it ordered against the key ups
    public readonly List<int> LockRestores = [];
    public void Dispose() { }
}

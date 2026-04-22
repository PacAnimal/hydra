using Hydra.Platform;

namespace Tests.Setup;

public sealed class FakeCursorVisibility : ICursorVisibility
{
    public bool IsHidden { get; private set; }
    public int HideCount { get; private set; }
    public int ShowCount { get; private set; }
    public CursorPosition Position { get; set; } = new(100, 100);

    public void HideCursor() { IsHidden = true; HideCount++; }
    public void ShowCursor() { IsHidden = false; ShowCount++; }
    public CursorPosition GetCursorPosition() => Position;
}

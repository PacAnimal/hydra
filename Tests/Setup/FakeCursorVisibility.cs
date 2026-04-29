using Hydra.Platform;

namespace Tests.Setup;

public sealed class FakeCursorVisibility : ICursor, ICursorHider
{
    public bool IsHidden { get; private set; }
    public int HideCount { get; private set; }
    public int ShowCount { get; private set; }

    public void Hide() { IsHidden = true; HideCount++; }
    public void Show() { IsHidden = false; ShowCount++; }

    public ValueTask HideCursor() { Hide(); return ValueTask.CompletedTask; }
    public ValueTask ShowCursor() { Show(); return ValueTask.CompletedTask; }
}

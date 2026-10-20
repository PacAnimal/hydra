using Hydra.Platform;

namespace Tests.Setup;

public sealed class FakeCursorVisibility : ICursor, ICursorHider
{
    public bool IsHidden { get; private set; }

    public void Hide() => IsHidden = true;
    public void Show() => IsHidden = false;

    public ValueTask HideCursor() { Hide(); return ValueTask.CompletedTask; }
    public ValueTask ShowCursor() { Show(); return ValueTask.CompletedTask; }
}

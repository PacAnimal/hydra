namespace Hydra.Platform;

public record CursorPosition(int X, int Y);

public interface ICursorVisibility
{
    void HideCursor();
    void ShowCursor();
    CursorPosition GetCursorPosition();
}

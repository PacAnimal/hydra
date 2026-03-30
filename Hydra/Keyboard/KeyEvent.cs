namespace Hydra.Keyboard;

public record KeyEvent(KeyEventType Type, uint KeyId, KeyModifiers Modifiers, ushort KeyButton);

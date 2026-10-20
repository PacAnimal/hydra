using Hydra.Keyboard;

namespace Tests.Setup;

// the default hotkey chord, Ctrl+Alt+Super, that every built-in action takes with its letter
public static class HotkeyChord
{
    public const KeyModifiers Modifiers = KeyModifiers.Control | KeyModifiers.Alt | KeyModifiers.Super;
}

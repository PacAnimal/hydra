namespace Hydra.Keyboard;

public sealed record KeyEvent(KeyEventType Type, KeyModifiers Modifiers)
{
    // exactly one of Character or Key is set per event.
    // Character: the receiver should type this character.
    // Key: the receiver should press this named key.
    public char? Character { get; init; }
    public SpecialKey? Key { get; init; }

    public static KeyEvent Char(KeyEventType type, char ch, KeyModifiers mods) =>
        new(type, mods) { Character = ch };

    public static KeyEvent Special(KeyEventType type, SpecialKey key, KeyModifiers mods) =>
        new(type, mods) { Key = key };
}

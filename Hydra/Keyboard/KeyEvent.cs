namespace Hydra.Keyboard;

public sealed record KeyEvent(KeyEventType Type, KeyModifiers Modifiers)
{
    // exactly one of Character or Key is set per event.
    // Character: the receiver should type this character.
    // Key: the receiver should press this named key.
    public char? Character { get; init; }
    public SpecialKey? Key { get; init; }
    public ushort? VkCode { get; init; }

    // true when this is an OS auto-repeat re-resolved with current modifier/dead-key state.
    // repeats are not initial presses: the slave injects them without tracking a new held key.
    public bool IsRepeat { get; init; }

    public static KeyEvent Char(KeyEventType type, char ch, KeyModifiers mods, ushort? vk = null) =>
        new(type, mods) { Character = ch, VkCode = vk };

    public static KeyEvent Special(KeyEventType type, SpecialKey key, KeyModifiers mods, ushort? vk = null) =>
        new(type, mods) { Key = key, VkCode = vk };
}

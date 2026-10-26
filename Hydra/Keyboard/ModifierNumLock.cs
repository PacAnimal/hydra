namespace Hydra.Keyboard;

// modifierNumLock, on a master: a keypad key pressed while Super, Control or Alt is held crosses as NumLock set to the
// setting would make it, true its digit (KP_4) and false its navigation key (KP_Left), with the NumLock bit to match,
// whatever the master's own NumLock. Every master's keys pass through here (InputRouter), so it is one rule for all of
// them; a macOS master's keypad always types digits, so only false changes anything there. The key keeps what it was
// pressed as through its repeats and up, whichever is let go first, so it never releases as a different key than it
// pressed as. The decimal key, which every resolver sends as its character with NumLock on, is known by
// IsKeypadDecimal: false makes it KP_Delete, and true leaves its character, which is what NumLock on types.
// Null, the default, leaves every key alone.
internal sealed class ModifierNumLock(bool? setting)
{
    private const KeyModifiers Trigger = KeyModifiers.Super | KeyModifiers.Control | KeyModifiers.Alt;

    // physical keys whose down went out under the setting, until their up
    private readonly HashSet<int> _held = [];

    internal KeyEvent Apply(KeyEvent keyEvent)
    {
        if (setting is not { } numLock || keyEvent.PhysicalKey == KeyEvent.Unstamped) return keyEvent;
        var key = keyEvent.Key ?? (keyEvent is { IsKeypadDecimal: true, Character: not null } ? SpecialKey.KP_Decimal : null);
        if (key is not { } pressed || !pressed.IsNumLockSensitive()) return keyEvent;

        if (keyEvent.Type == KeyEventType.KeyUp)
        {
            if (!_held.Remove(keyEvent.PhysicalKey)) return keyEvent;
        }
        else if (keyEvent.IsRepeat)
        {
            if (!_held.Contains(keyEvent.PhysicalKey)) return keyEvent;
        }
        else if ((keyEvent.Modifiers & Trigger) != 0)
            _held.Add(keyEvent.PhysicalKey);
        else
        {
            _held.Remove(keyEvent.PhysicalKey);   // a plain press goes as the master's own NumLock says
            return keyEvent;
        }

        var mods = numLock ? keyEvent.Modifiers | KeyModifiers.NumLock : keyEvent.Modifiers & ~KeyModifiers.NumLock;
        if (keyEvent.Key is null && numLock) return keyEvent with { Modifiers = mods };   // the decimal's character already
        return keyEvent with { Key = pressed.WithNumLock(numLock), Character = null, Modifiers = mods };
    }
}

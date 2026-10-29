using Hydra.Keyboard;

namespace Hydra.Platform.Linux;

// What an X11 slave injects for a keypad key. Keypad navigation pressed with Shift goes in as its dedicated key: X11
// spends Shift on choosing a keypad key's level, so apps would see no Shift to extend a selection with. A key's repeats
// and up inject whatever its press did, whatever Shift is doing by then.
internal sealed class ShiftedKeypad
{
    private readonly Dictionary<SpecialKey, SpecialKey> _pressedAs = [];

    internal SpecialKey Injected(SpecialKey key, KeyEventType type, KeyModifiers mods)
    {
        if (type == KeyEventType.KeyUp) return _pressedAs.Remove(key, out var released) ? released : key;
        if (_pressedAs.TryGetValue(key, out var held)) return held;
        var dedicated = key.WithoutKeypad();
        if (dedicated == key) return key;
        return _pressedAs[key] = (mods & KeyModifiers.Shift) != 0 ? dedicated : key;
    }
}

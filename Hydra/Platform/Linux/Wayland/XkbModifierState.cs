namespace Hydra.Platform.Linux.Wayland;

internal sealed record XkbModifiers(uint Depressed, uint Latched, uint Locked, uint Group);

// the modifier state a keymap derives from the keys pressed on it, as zwp_virtual_keyboard_v1.modifiers takes it:
// a compositor that does not derive modifiers from a virtual keyboard's keys (Hyprland) is told them by the client
internal sealed class XkbModifierState : IDisposable
{
    private readonly Lock _lock = new();
    private readonly XkbKeymap _keymap;
    private readonly bool _ownsKeymap;
    private nint _state;

    internal XkbModifierState(string keymapText)
        : this(XkbKeymap.FromText(keymapText), true)
    {
    }

    // tracks on a keymap the caller owns and disposes after this state
    internal XkbModifierState(XkbKeymap keymap)
        : this(keymap, false)
    {
    }

    private XkbModifierState(XkbKeymap keymap, bool ownsKeymap)
    {
        _keymap = keymap;
        _ownsKeymap = ownsKeymap;
        _state = EvdevNativeMethods.xkb_state_new(keymap.Handle);
        if (_state != 0) return;
        if (ownsKeymap) keymap.Dispose();
        throw new InvalidOperationException("Failed to create xkb state.");
    }

    internal XkbModifiers Current
    {
        get
        {
            lock (_lock)
            {
                if (_state == 0) return new XkbModifiers(0, 0, 0, 0);
                return new XkbModifiers(
                    EvdevNativeMethods.xkb_state_serialize_mods(_state, EvdevNativeMethods.XKB_STATE_MODS_DEPRESSED),
                    EvdevNativeMethods.xkb_state_serialize_mods(_state, EvdevNativeMethods.XKB_STATE_MODS_LATCHED),
                    EvdevNativeMethods.xkb_state_serialize_mods(_state, EvdevNativeMethods.XKB_STATE_MODS_LOCKED),
                    EvdevNativeMethods.xkb_state_serialize_layout(_state, EvdevNativeMethods.XKB_STATE_LAYOUT_EFFECTIVE));
            }
        }
    }

    // a key of the keymap went down or up; true when that changed the modifiers or the group
    internal bool Update(uint evdevKey, bool pressed)
    {
        lock (_lock)
        {
            if (_state == 0) return false;
            // evdev keycode + 8 = xkb keycode
            var direction = pressed ? EvdevNativeMethods.XKB_KEY_DOWN : EvdevNativeMethods.XKB_KEY_UP;
            return EvdevNativeMethods.xkb_state_update_key(_state, evdevKey + 8, direction) != 0;
        }
    }

    // the bit a named modifier (Shift, Control, Mod1, Mod4, Mod5, Lock, ...) sets in a mask; 0 when the keymap lacks it
    internal uint Mask(string name)
    {
        lock (_lock)
        {
            return _state == 0 ? 0 : _keymap.ModMask(name);
        }
    }

    public void Dispose()
    {
        lock (_lock)
        {
            if (_state == 0) return;
            EvdevNativeMethods.xkb_state_unref(_state);
            _state = 0;
            if (_ownsKeymap) _keymap.Dispose();
        }
    }
}

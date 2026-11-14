using System.Text;
using Hydra.Config;
using Hydra.Keyboard;

namespace Hydra.Platform.Linux.Wayland;

// Hyprland's config language: hyprland.lua from 0.56 on, hyprland.conf before it
internal enum HyprlandConfig { Lua, Conf }

// one hotkey chord bound as a global shortcut, its id naming the action and the chord's place among the action's
internal sealed record ShortcutChord(string Id, HotkeyAction Action, int Index, HotkeyBinding Binding);

// the evdev keys a chord is pressed with: its modifiers in order, then the key
internal sealed record ChordKeys(IReadOnlyList<uint> Modifiers, uint Key)
{
    internal IEnumerable<uint> Downs => [.. Modifiers, Key];

    internal IEnumerable<uint> Ups => Downs.Reverse();
}

// The hotkeys as GlobalShortcuts, for a Wayland master at home, where no key reaches EIS. Each chord's
// preferred_trigger is in the XDG shortcuts spec's syntax: the modifiers CTRL, ALT, SHIFT and LOGO joined by '+' and
// ended by the key's xkbcommon keysym name. AltGr has no such modifier and Hydra's own keys no keysym, so those chords
// are bound with no trigger for the user to pick one. An activation is pressed as the chord's own evdev keys.
internal static class WaylandShortcuts
{
    // what does something at home. The monitor switch warps (WarpCursor is a no-op here) or enters a remote with no
    // capture under it, and relative mouse and Mission Control act on a visit only, whose keys come through EIS
    private static readonly HotkeyAction[] AtHome =
        [HotkeyAction.ToggleCursorLock, HotkeyAction.LockSlaves, HotkeyAction.CopyFiles, HotkeyAction.PasteFiles];

    internal static IReadOnlyList<ShortcutChord> Chords(HotkeyBindings hotkeys)
    {
        var chords = new List<ShortcutChord>();
        foreach (var action in AtHome)
        {
            var bindings = hotkeys.For(action);
            for (var i = 0; i < bindings.Count; i++)
                chords.Add(new ShortcutChord($"{action}-{i}", action, i, bindings[i]));
        }
        return chords;
    }

    internal static ShortcutRequest Request(ShortcutChord chord)
    {
        var description = Description(chord.Action);
        return new ShortcutRequest(chord.Id, chord.Index == 0 ? description : $"{description} ({chord.Index + 1})", Trigger(chord.Binding));
    }

    private static string Description(HotkeyAction action) => action switch
    {
        HotkeyAction.ToggleCursorLock => "Lock the cursor to its screen",
        HotkeyAction.LockSlaves => "Lock every slave",
        HotkeyAction.CopyFiles => "Copy files",
        HotkeyAction.PasteFiles => "Paste files",
        _ => action.ToString(),
    };

    // the chord in the XDG shortcuts spec's syntax; null when it has no spelling there
    internal static string? Trigger(HotkeyBinding binding) => Trigger(binding.Modifiers, KeysymName(Keysym(binding)));

    internal static string? Trigger(KeyModifiers modifiers, string? keyName)
    {
        if (keyName is null || (modifiers & KeyModifiers.AltGr) != 0) return null;
        var parts = new List<string>(5);
        if ((modifiers & KeyModifiers.Control) != 0) parts.Add("CTRL");
        if ((modifiers & KeyModifiers.Alt) != 0) parts.Add("ALT");
        if ((modifiers & KeyModifiers.Shift) != 0) parts.Add("SHIFT");
        if ((modifiers & KeyModifiers.Super) != 0) parts.Add("LOGO");
        parts.Add(keyName);
        return string.Join('+', parts);
    }

    // the line binding a shortcut in Hyprland's config, its chord from the trigger asked for. "CTRL+ALT+LOGO+l" is
    // hyprland.lua's (0.56 and later) "hl.bind("CTRL + ALT + SUPER + L", hl.dsp.global("app:id"))", a letter upper case
    // as the hyprland lane binds it, and hyprland.conf's (older) "bind = CTRL ALT SUPER, l, global, app:id";
    // placeholders for a chord the trigger syntax cannot say
    internal static string HyprlandBind(HyprlandConfig config, string appId, string id, string? trigger)
    {
        var parts = trigger?.Split('+') ?? [];
        var mods = parts.Length > 0 ? parts[..^1].Select(m => m == "LOGO" ? "SUPER" : m).ToList() : ["<mods>"];
        var key = parts.Length > 0 ? parts[^1] : "<key>";
        if (config == HyprlandConfig.Conf) return $"bind = {string.Join(' ', mods)}, {key}, global, {appId}:{id}";
        if (key.Length == 1 && char.IsAsciiLetter(key[0])) key = key.ToUpperInvariant();
        return $"hl.bind(\"{string.Join(" + ", [.. mods, key])}\", hl.dsp.global(\"{appId}:{id}\"))";
    }

    // the chord's key as a keysym, a character through xkbcommon as the keymap lookup takes it; 0 when it has none
    internal static uint Keysym(HotkeyBinding binding) =>
        binding.Key is { } special ? XorgKeysym.Of(special) is var keysym and > 0 and <= uint.MaxValue ? (uint)keysym : 0
        : binding.Character is { } ch ? XkbKeymapLookup.CharToKeysym(ch) : 0;

    // xkbcommon's name for a keysym, as the shortcuts spec names keys; null for none
    internal static string? KeysymName(uint keysym)
    {
        if (keysym == 0) return null;
        var buffer = new byte[64];
        var length = EvdevNativeMethods.xkb_keysym_get_name(keysym, buffer, (nuint)buffer.Length);
        return length is > 0 and < 64 ? Encoding.UTF8.GetString(buffer, 0, length) : null;
    }

    // the chord's keys: each modifier's key on the keymap, then key; null when the keymap has no key
    internal static ChordKeys? Keys(IKeymapKeys keymap, KeyModifiers modifiers, uint? key)
    {
        if (key is not { } k) return null;
        var keys = new List<uint>(5);
        if ((modifiers & KeyModifiers.Control) != 0)
            keys.Add(ModifierKey(keymap, EvdevNativeMethods.KEY_LEFTCTRL, XorgVirtualKey.Control_L, XorgVirtualKey.Control_R));
        if ((modifiers & KeyModifiers.Alt) != 0) keys.Add(ModifierKey(keymap, EvdevNativeMethods.KEY_LEFTALT, XorgVirtualKey.Alt_L, XorgVirtualKey.Alt_R));
        if ((modifiers & KeyModifiers.Shift) != 0)
            keys.Add(ModifierKey(keymap, EvdevNativeMethods.KEY_LEFTSHIFT, XorgVirtualKey.Shift_L, XorgVirtualKey.Shift_R));
        if ((modifiers & KeyModifiers.Super) != 0)
            keys.Add(ModifierKey(keymap, EvdevNativeMethods.KEY_LEFTMETA, XorgVirtualKey.Super_L, XorgVirtualKey.Super_R));
        if ((modifiers & KeyModifiers.AltGr) != 0)
            keys.Add(ModifierKey(keymap, EvdevNativeMethods.KEY_RIGHTALT, XorgVirtualKey.ISO_Level3_Shift, XorgVirtualKey.Mode_switch));
        return new ChordKeys(keys, k);
    }

    // the key typing the first keysym the keymap has, alone (the left-hand one before the right): the usual key when
    // it does (the keymap may also have it on a virtual key, such as Super_L on <SUPR>), else wherever an xkb option or
    // the layout put it, else the usual key
    private static uint ModifierKey(IKeymapKeys keymap, uint usual, params ReadOnlySpan<ulong> keysyms)
    {
        foreach (var keysym in keysyms)
        {
            if (keymap.BaseKeysym(usual) == keysym) return usual;
            // a key typing it only on a higher level needs a modifier the chord does not press
            if (keymap.EvdevKeyFor((uint)keysym) is { } code && keymap.BaseKeysym(code) == keysym) return code;
        }
        return usual;
    }

    // the key events a keyboard pressing (or releasing) keys in that order makes through the resolver
    internal static List<KeyEvent> Events(EvdevKeyResolver resolver, IEnumerable<uint> keys, bool down)
    {
        var events = new List<KeyEvent>();
        foreach (var key in keys)
        {
            var resolved = resolver.Resolve(key, down ? 1 : 0);
            if (resolved is null) continue;
            foreach (var keyEvent in resolved)
                if (keyEvent is not null) events.Add(keyEvent);
        }
        return events;
    }
}

// The chords a shortcut activation pressed and its deactivation has yet to release. Two may overlap (a second chord
// pressed under the first's modifiers): a key is pressed once while any chord holds it, and released only when the last
// chord holding it lets go.
internal sealed class HeldChords
{
    private readonly List<Held> _held = [];

    // the keys to press for id, in order: those no held chord has down already; none when id is held already
    internal IReadOnlyList<uint> Press(string id, ChordKeys keys)
    {
        if (_held.Any(h => h.Id == id)) return [];
        var down = Down();
        _held.Add(new Held(id, keys));
        return [.. keys.Downs.Where(k => !down.Contains(k)).Distinct()];
    }

    // the keys to release for id, in order: its own, the main key first, less those another held chord still holds
    internal IReadOnlyList<uint> Release(string id)
    {
        var index = _held.FindIndex(h => h.Id == id);
        if (index < 0) return [];
        var keys = _held[index].Keys;
        _held.RemoveAt(index);
        var still = Down();
        return [.. keys.Ups.Where(k => !still.Contains(k)).Distinct()];
    }

    // every held key, the last chord pressed first
    internal IReadOnlyList<uint> ReleaseAll()
    {
        var ups = new List<uint>();
        while (_held.Count > 0) ups.AddRange(Release(_held[^1].Id));
        return ups;
    }

    private HashSet<uint> Down() => [.. _held.SelectMany(h => h.Keys.Downs)];

    private sealed record Held(string Id, ChordKeys Keys);
}

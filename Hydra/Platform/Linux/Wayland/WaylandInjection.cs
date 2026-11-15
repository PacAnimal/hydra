using System.Text;
using Hydra.Keyboard;
using Hydra.Mouse;
using Hydra.Relay;
using Hydra.Screen;

namespace Hydra.Platform.Linux.Wayland;

// one evdev key press or release sent over EIS
internal readonly record struct EiKeyStroke(uint EvdevKey, bool Pressed);

// what a key's press sends: Key is the evdev key held until its up, Strokes the presses and releases around it,
// NumLock the lock state the key must be pressed under, null to leave NumLock alone, and Group the layout group its
// keys type it in, null for a key every group types alike (a special key, a bound one)
internal sealed record EiKeyPlan(uint Key, IReadOnlyList<EiKeyStroke> Strokes, bool? NumLock = null, uint? Group = null);

// the evdev keys of the Shift and AltGr keys the slave holds down for the master
internal sealed record HeldModifierKeys(IReadOnlyList<uint> Shift, IReadOnlyList<uint> AltGr)
{
    internal static readonly HeldModifierKeys None = new([], []);
}

internal readonly record struct EiPoint(double X, double Y);

internal readonly record struct EiScroll(int X, int Y);

// the pure translation behind WaylandOutputHandler: Hydra's events to what an ei sender emits
internal static class WaylandInjection
{
    private const KeyModifiers ShortcutModifiers = KeyModifiers.Control | KeyModifiers.Alt | KeyModifiers.Super;
    private const uint Space = 0x20;

    // a key's press on the compositor's keymap; null when the keymap cannot type it. capsLock is the slave's; group
    // is the layout group keys go in under, preferred for a character, and anyGroup lets a character from another
    // group be planned, for a device that can select that group around the key
    internal static EiKeyPlan? Plan(XkbKeymapLookup keymap, KeyEventMessage msg, HeldModifierKeys held, bool capsLock, uint group = 0,
        bool anyGroup = false)
    {
        if (msg.Character is { } ch) return PlanCharacter(keymap, ch, msg.Modifiers, held, capsLock, group, anyGroup);
        if (msg.Key is not { } key || SpecialKeyCode(keymap, key) is not { } code) return null;
        // a keypad key's level is NumLock XOR Shift, as on X11, so it is synced to what the key needs
        return new EiKeyPlan(code, [new EiKeyStroke(code, true)], XorgKeyResolver.KeypadNumLock(key, (msg.Modifiers & KeyModifiers.Shift) != 0));
    }

    // a special or modifier key's own evdev key, left and right apart; a side the keymap lacks (Alt_R where AltGr
    // sits) goes in as its twin, which X11 would temp-bind
    internal static uint? SpecialKeyCode(XkbKeymapLookup keymap, SpecialKey key) =>
        KeysymCode(keymap, XorgKeysym.Injected(key)) ?? (OtherSide(key) is { } twin ? KeysymCode(keymap, XorgKeysym.Of(twin)) : null);

    private static SpecialKey? OtherSide(SpecialKey key) => key switch
    {
        SpecialKey.Shift_R => SpecialKey.Shift_L,
        SpecialKey.Control_R => SpecialKey.Control_L,
        SpecialKey.Alt_R => SpecialKey.Alt_L,
        SpecialKey.Super_R => SpecialKey.Super_L,
        _ => null,
    };

    // the evdev key a keysym is on, whatever level: a special or modifier key's own key (left and right apart),
    // its level being the lock keys' business
    internal static uint? KeysymCode(XkbKeymapLookup keymap, ulong keysym) =>
        keysym is 0 or > uint.MaxValue ? null : keymap.EvdevKeyFor((uint)keysym);

    // a held key's release. a Super released from a hotkey chord first taps a key between its press and release, so
    // GNOME and KDE take it as used and open no overview for it, as the X11 slave taps VoidSymbol: a key that types
    // nothing, else Shift_L, which neither desktop binds alone
    internal static IReadOnlyList<EiKeyStroke> Release(XkbKeymapLookup keymap, KeyEventMessage msg, uint heldKey)
    {
        if (msg.Chorded != true || msg.Key is not (SpecialKey.Super_L or SpecialKey.Super_R)
            || (keymap.UnboundKey() ?? KeysymCode(keymap, XorgVirtualKey.Shift_L)) is not { } tap)
            return [new EiKeyStroke(heldKey, false)];
        return [new EiKeyStroke(tap, true), new EiKeyStroke(tap, false), new EiKeyStroke(heldKey, false)];
    }

    // a character on the key that types it, with Shift and AltGr pressed around it when the slave does not hold
    // them, and the held ones it does not want lifted around it unless a shortcut is being typed. the given group's
    // key is preferred; another group's only under anyGroup. a shortcut takes the given group's key where that group is
    // Latin (Ctrl+z on QWERTZ's z key, as the desktop there matches it), else the first Latin group's (else group 0's),
    // any group's where that has none: on a device that cannot select it the key goes in under the given group, as apps
    // match Ctrl+С on a non-Latin layout as Ctrl+C, and the Latin key keeps a Ctrl+, from gaining the Shift ru's comma
    // needs. a character no key types is composed with the keymap's dead keys where it can be, as Deskflow does, in
    // the given group only, since a sequence goes in under one group
    private static EiKeyPlan? PlanCharacter(XkbKeymapLookup keymap, char ch, KeyModifiers mods, HeldModifierKeys held, bool capsLock,
        uint group, bool anyGroup)
    {
        var lift = !IsShortcut(mods);
        var preferred = lift || keymap.IsLatinGroup(group) ? group : keymap.LatinGroup ?? 0;
        var keysym = XkbKeymapLookup.CharToKeysym(ch);

        if (keymap.Find(keysym, preferred) is { } stroke && (anyGroup || !lift || stroke.Group == group))
        {
            var single = new List<EiKeyStroke>();
            if (!Tap(keymap, stroke, IsCased(ch), held, lift, capsLock, last: true, single)) return null;
            // a character only a keypad level types needs NumLock on
            return new EiKeyPlan(stroke.EvdevKey, single, stroke.NumLock ? true : null, stroke.Group);
        }

        if (DeadKeySequence(keymap, ch, group) is not { } sequence) return null;
        var strokes = new List<EiKeyStroke>();
        var key = 0u;
        for (var i = 0; i < sequence.Count; i++)
        {
            var step = sequence[i];
            // NumLock is synced only for a key typed alone, so a keypad level is no part of a sequence
            if (keymap.Find(step.Keysym, group) is not { NumLock: false } found || found.Group != group) return null;
            if (!Tap(keymap, found, step.Cased, held, lift, capsLock, i == sequence.Count - 1, strokes)) return null;
            key = found.EvdevKey;
        }
        return new EiKeyPlan(key, strokes, Group: group);
    }

    // a shortcut's character on the us reference keymap's key, for a keymap with no Latin group whose device cannot
    // bind it: apps match Ctrl+С on that key as Ctrl+C. null where the reference has none
    internal static EiKeyPlan? PlanReference(XkbKeymapLookup keymap, char ch, HeldModifierKeys held, bool capsLock)
    {
        if (LatinShortcutKeysym.ReferenceKeyFor(XkbKeymapLookup.CharToKeysym(ch)) is not { } reference) return null;
        var strokes = new List<EiKeyStroke>();
        return Tap(keymap, reference, IsCased(ch), held, lift: false, capsLock, last: true, strokes) ? new EiKeyPlan(reference.EvdevKey, strokes) : null;
    }

    // a character typed with Ctrl, Alt or Super is a shortcut; an AltGr character may carry Windows' Ctrl beside it,
    // which is none
    internal static bool IsShortcut(KeyModifiers mods) =>
        (mods & ((mods & KeyModifiers.AltGr) != 0 ? KeyModifiers.Super : ShortcutModifiers)) != 0;

    // a character a key types only on a layout group other than the effective one, which a device that cannot select
    // the group must not type: under the effective group that key types something else
    internal static bool OnOtherGroup(XkbKeymapLookup keymap, char ch, uint group) =>
        keymap.Find(XkbKeymapLookup.CharToKeysym(ch), group) is { } stroke && stroke.Group != group;

    // one key of a character's plan, its modifiers around it: the last is left down, as the master holds it
    private static bool Tap(XkbKeymapLookup keymap, XkbKeyStroke stroke, bool cased, HeldModifierKeys held, bool lift, bool capsLock,
        bool last, List<EiKeyStroke> strokes)
    {
        // CapsLock acts as Shift on a letter's first two levels
        var wantShift = stroke.Shift;
        if (capsLock && !stroke.AltGr && cased) wantShift = !wantShift;

        var before = new List<EiKeyStroke>();
        var after = new List<EiKeyStroke>();
        if (!Wrap(keymap, XorgVirtualKey.Shift_L, wantShift, held.Shift, lift, before, after)) return false;
        if (!Wrap(keymap, XorgVirtualKey.ISO_Level3_Shift, stroke.AltGr, held.AltGr, lift, before, after)) return false;

        strokes.AddRange(before);
        strokes.Add(new EiKeyStroke(stroke.EvdevKey, true));
        if (!last) strokes.Add(new EiKeyStroke(stroke.EvdevKey, false));
        after.Reverse();
        strokes.AddRange(after);
        return true;
    }

    private static bool IsCased(char ch) => char.ToUpperInvariant(ch) != char.ToLowerInvariant(ch);

    // a keysym of a dead-key sequence; Cased when it is a letter CapsLock shifts
    internal readonly record struct ComposeStep(uint Keysym, bool Cased);

    // a spacing accent's dead key and the keysym typed after it
    private readonly record struct SpacingKeys(uint Dead, uint Then);

    // the keysyms that compose a character no key types, as the Compose table takes them: a spacing accent as its
    // dead key then space (´ and ¨ as their dead key twice, since dead_acute space is an apostrophe), else the
    // character's canonical decomposition, its last mark's dead key first, down to a character a key types (ấ is
    // dead_acute dead_circumflex a, or dead_acute â where â has a key). null when the keymap cannot compose it
    internal static IReadOnlyList<ComposeStep>? DeadKeySequence(XkbKeymapLookup keymap, char ch, uint group = 0)
    {
        if (SpacingAccent(ch) is { } spacing)
        {
            if (!InGroup(keymap, spacing.Dead, group) || !InGroup(keymap, spacing.Then, group)) return null;
            return [new ComposeStep(spacing.Dead, false), new ComposeStep(spacing.Then, false)];
        }
        if (char.IsSurrogate(ch)) return null;
        var decomposed = ch.ToString().Normalize(NormalizationForm.FormD);
        return decomposed.Length < 2 ? null : Compose(keymap, decomposed, group, whole: true);
    }

    // whole is the character asked for, composed even where a key types it; a part of it is typed on its key if any
    private static List<ComposeStep>? Compose(XkbKeymapLookup keymap, string decomposed, uint group, bool whole = false)
    {
        var composed = decomposed.Normalize(NormalizationForm.FormC);
        if (!whole && composed.Length == 1 && InGroup(keymap, XkbKeymapLookup.CharToKeysym(composed[0]), group))
            return [new ComposeStep(XkbKeymapLookup.CharToKeysym(composed[0]), IsCased(composed[0]))];
        if (decomposed.Length < 2 || DeadKeysym(decomposed[^1]) is not { } dead || !InGroup(keymap, dead, group)) return null;
        if (Compose(keymap, decomposed[..^1], group) is not { } rest) return null;
        rest.Insert(0, new ComposeStep(dead, false));
        return rest;
    }

    private static bool InGroup(XkbKeymapLookup keymap, uint keysym, uint group) => keymap.Find(keysym, group) is { } stroke && stroke.Group == group;

    // the dead key a combining mark is typed with (XKB's dead_* keysyms); null for a mark no dead key types
    internal static uint? DeadKeysym(char mark) => mark switch
    {
        '\u0300' => 0xFE50, // dead_grave
        '\u0301' => 0xFE51, // dead_acute
        '\u0302' => 0xFE52, // dead_circumflex
        '\u0303' => 0xFE53, // dead_tilde
        '\u0304' => 0xFE54, // dead_macron
        '\u0306' => 0xFE55, // dead_breve
        '\u0307' => 0xFE56, // dead_abovedot
        '\u0308' => 0xFE57, // dead_diaeresis
        '\u030A' => 0xFE58, // dead_abovering
        '\u030B' => 0xFE59, // dead_doubleacute
        '\u030C' => 0xFE5A, // dead_caron
        '\u0327' => 0xFE5B, // dead_cedilla
        '\u0328' => 0xFE5C, // dead_ogonek
        '\u0345' => 0xFE5D, // dead_iota
        '\u0323' => 0xFE60, // dead_belowdot
        '\u0309' => 0xFE61, // dead_hook
        '\u031B' => 0xFE62, // dead_horn
        '\u0313' => 0xFE64, // dead_abovecomma
        '\u0314' => 0xFE65, // dead_abovereversedcomma
        '\u030F' => 0xFE66, // dead_doublegrave
        '\u0325' => 0xFE67, // dead_belowring
        '\u0331' => 0xFE68, // dead_belowmacron
        '\u032D' => 0xFE69, // dead_belowcircumflex
        '\u0330' => 0xFE6A, // dead_belowtilde
        '\u032E' => 0xFE6B, // dead_belowbreve
        '\u0324' => 0xFE6C, // dead_belowdiaeresis
        '\u0311' => 0xFE6D, // dead_invertedbreve
        '\u0326' => 0xFE6E, // dead_belowcomma
        _ => null,
    };

    // a spacing accent and what follows its dead key to type it, as xkbcommon's en_US Compose table has them
    private static SpacingKeys? SpacingAccent(char ch) => ch switch
    {
        '`' => new SpacingKeys(0xFE50, Space),
        '´' => new SpacingKeys(0xFE51, 0xFE51),
        '^' => new SpacingKeys(0xFE52, Space),
        '~' => new SpacingKeys(0xFE53, Space),
        '¯' => new SpacingKeys(0xFE54, Space),
        '˘' => new SpacingKeys(0xFE55, Space),
        '˙' => new SpacingKeys(0xFE56, Space),
        '¨' => new SpacingKeys(0xFE57, 0xFE57),
        '°' => new SpacingKeys(0xFE58, Space),
        '˝' => new SpacingKeys(0xFE59, Space),
        'ˇ' => new SpacingKeys(0xFE5A, Space),
        '¸' => new SpacingKeys(0xFE5B, Space),
        '˛' => new SpacingKeys(0xFE5C, Space),
        _ => null,
    };

    // a character on a key bound to it alone: the bound key types it at every level, so no modifier is touched
    internal static EiKeyPlan PlanBound(uint evdevKey) => new(evdevKey, [new EiKeyStroke(evdevKey, true)]);

    // false when the modifier is wanted, not held and not on the keymap
    private static bool Wrap(XkbKeymapLookup keymap, ulong modifierKeysym, bool want, IReadOnlyList<uint> held, bool lift,
        List<EiKeyStroke> before, List<EiKeyStroke> after)
    {
        if (want && held.Count == 0)
        {
            if (keymap.EvdevKeyFor((uint)modifierKeysym) is not { } code) return false;
            before.Add(new EiKeyStroke(code, true));
            after.Add(new EiKeyStroke(code, false));
        }
        else if (!want && held.Count > 0 && lift)
        {
            foreach (var code in held)
            {
                before.Add(new EiKeyStroke(code, false));
                after.Add(new EiKeyStroke(code, true));
            }
        }
        return true;
    }

    internal static uint? EvdevButton(MouseButton button) => EvdevButtons.Code(button);

    // ei's discrete scroll is 120 a detent like the wire's (less for a high-resolution wheel), positive down and
    // right; the wire's positive Y is up (X11's button 4) and its positive X right
    internal static EiScroll Discrete(MouseScrollMessage msg) => new(msg.XDelta, -msg.YDelta);

    // absolute coordinates are logical pixels in the regions' own space, which is the space the slave's screens are
    // reported in; a point outside every region is pulled onto the nearest one
    internal static EiPoint AbsoluteTarget(IReadOnlyList<ScreenBounds> regions, int x, int y)
    {
        if (regions.Count == 0) return new EiPoint(x, y);
        EiPoint? best = null;
        var bestDistance = double.MaxValue;
        foreach (var region in regions)
        {
            if (region.Width <= 0 || region.Height <= 0) continue;
            var left = (double)region.X;
            var top = (double)region.Y;
            var cx = Math.Clamp(x, left, left + region.Width - 1);
            var cy = Math.Clamp(y, top, top + region.Height - 1);
            var distance = (cx - x) * (cx - x) + (cy - y) * (cy - y);
            if (distance >= bestDistance) continue;
            bestDistance = distance;
            best = new EiPoint(cx, cy);
        }
        return best ?? new EiPoint(x, y);
    }
}

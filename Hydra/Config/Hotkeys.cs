using Cathedral.Extensions;
using Hydra.Keyboard;

namespace Hydra.Config;

// what a hotkey can be bound to. master-side only: hotkeys are consumed in InputRouter before anything
// is forwarded, so a slave never sees one and has no use for the config.
public enum HotkeyAction
{
    ToggleCursorLock,
    LockSlaves,
    ToggleRelativeMouse,
    CopyFiles,
    PasteFiles,
    MissionControl,
}

// one parsed hotkey: the modifiers that must be held, plus either a named key or a literal character.
public sealed record HotkeyBinding(KeyModifiers Modifiers, SpecialKey? Key, char? Character)
{
    // lock states are never part of a chord. CapsLock being on must not stop Ctrl+Alt+Super+L matching,
    // and binding ScrollLock is self-defeating otherwise: pressing it sets its own lock bit on the very
    // event meant to trigger the hotkey.
    private const KeyModifiers LockStates = KeyModifiers.CapsLock | KeyModifiers.NumLock | KeyModifiers.ScrollLock;

    private static readonly Dictionary<string, KeyModifiers> ModifierNames = new(StringComparer.OrdinalIgnoreCase)
    {
        ["shift"] = KeyModifiers.Shift,
        ["ctrl"] = KeyModifiers.Control,
        ["control"] = KeyModifiers.Control,
        ["alt"] = KeyModifiers.Alt,
        ["opt"] = KeyModifiers.Alt,
        ["option"] = KeyModifiers.Alt,
        ["super"] = KeyModifiers.Super,
        ["win"] = KeyModifiers.Super,
        ["windows"] = KeyModifiers.Super,
        ["cmd"] = KeyModifiers.Super,
        ["command"] = KeyModifiers.Super,
        ["meta"] = KeyModifiers.Super,
        ["altgr"] = KeyModifiers.AltGr,
    };

    // keys the master reports as characters, which therefore have no SpecialKey name. spelling them out
    // is the only way to write some of them ("Ctrl+Space") and far clearer for the rest ("Ctrl+Plus").
    private static readonly Dictionary<string, char> CharacterNames = new(StringComparer.OrdinalIgnoreCase)
    {
        ["space"] = ' ',
        ["plus"] = '+',
        ["minus"] = '-',
        ["comma"] = ',',
        ["period"] = '.',
        ["dot"] = '.',
        ["slash"] = '/',
        ["backslash"] = '\\',
        ["semicolon"] = ';',
        ["apostrophe"] = '\'',
        ["quote"] = '\'',
        ["grave"] = '`',
        ["backtick"] = '`',
        ["equal"] = '=',
        ["equals"] = '=',
        ["bracketleft"] = '[',
        ["bracketright"] = ']',
    };

    public bool Matches(KeyEvent ev)
    {
        if ((ev.Modifiers & ~LockStates) != Modifiers) return false;
        if (Key.HasValue) return ev.Key == Key.Value;
        return ev.Character.HasValue && char.ToLowerInvariant(ev.Character.Value) == Character;
    }

    public static bool TryParse(string text, out HotkeyBinding? binding, out string? error)
    {
        binding = null;
        error = null;

        var tokens = Split(text);
        if (tokens.Count == 0)
        {
            error = "is empty";
            return false;
        }

        var mods = KeyModifiers.None;
        string? keyToken = null;
        foreach (var token in tokens)
        {
            if (ModifierNames.TryGetValue(token, out var mod))
            {
                mods |= mod;
                continue;
            }
            if (keyToken != null)
            {
                error = $"names two keys ('{keyToken}' and '{token}') -- a hotkey has exactly one, plus modifiers";
                return false;
            }
            keyToken = token;
        }

        if (keyToken == null)
        {
            error = "is only modifiers, with no key to press";
            return false;
        }

        // Enum.TryParse also accepts the underlying number, so "4" would parse as (SpecialKey)4 rather
        // than the character '4'. require a name, and one that is actually defined.
        if (!char.IsAsciiDigit(keyToken[0])
            && Enum.TryParse<SpecialKey>(keyToken, ignoreCase: true, out var special)
            && Enum.IsDefined(special))
        {
            // SpecialKey.IsModifier() also covers the lock keys, and ScrollLock is the single most
            // requested binding here, so only the keys that exist to modify another key are refused.
            if (special is SpecialKey.Shift_L or SpecialKey.Shift_R or SpecialKey.Control_L or SpecialKey.Control_R
                or SpecialKey.Alt_L or SpecialKey.Alt_R or SpecialKey.Super_L or SpecialKey.Super_R or SpecialKey.AltGr)
            {
                error = $"binds the modifier '{keyToken}' as its key, which would swallow every press of it";
                return false;
            }
            binding = new HotkeyBinding(mods, special, null);
            return true;
        }

        if (CharacterNames.TryGetValue(keyToken, out var namedChar))
            return Printable(mods, namedChar, keyToken, out binding, out error);

        if (keyToken.Length != 1)
        {
            error = $"has no key named '{keyToken}' -- see the key list in docs/CONFIGURATION.md";
            return false;
        }

        return Printable(mods, keyToken[0], keyToken, out binding, out error);
    }

    // a printable key is consumed on both key-down and key-up, so binding one bare would eat every press
    // of it while Hydra runs. named keys (ScrollLock, F13) are the point of this and stay legal alone.
    private static bool Printable(KeyModifiers mods, char ch, string keyToken, out HotkeyBinding? binding, out string? error)
    {
        binding = null;
        error = null;
        if (mods == KeyModifiers.None)
        {
            error = $"binds '{keyToken}' with no modifiers, which would stop that character ever being typed";
            return false;
        }
        binding = new HotkeyBinding(mods, null, char.ToLowerInvariant(ch));
        return true;
    }

    // splits on '+' while allowing '+' itself as the bound key ("Ctrl++", or "+" on its own)
    private static List<string> Split(string text)
    {
        var trimmed = text.Trim();
        if (trimmed == "+") return ["+"];

        // a trailing "++" is a separator followed by the '+' key; drop the separator and keep the key
        var plusIsKey = trimmed.EndsWith("++", StringComparison.Ordinal);
        if (plusIsKey) trimmed = trimmed[..^1];

        var tokens = trimmed.Split('+').Select(token => token.Trim()).Where(token => token.Length > 0).ToList();
        if (plusIsKey) tokens.Add("+");
        return tokens;
    }
}

// the active hotkey table: every action, and the one or more chords bound to it.
public sealed class HotkeyBindings
{
    private static readonly Dictionary<HotkeyAction, string[]> Defaults = new()
    {
        [HotkeyAction.ToggleCursorLock] = ["Ctrl+Alt+Super+L"],
        [HotkeyAction.LockSlaves] = ["Ctrl+Alt+Super+K"],
        [HotkeyAction.ToggleRelativeMouse] = ["Ctrl+Alt+Super+M"],
        [HotkeyAction.CopyFiles] = ["Ctrl+Alt+Super+C"],
        [HotkeyAction.PasteFiles] = ["Ctrl+Alt+Super+V"],
        [HotkeyAction.MissionControl] = ["Ctrl+Alt+Super+Z"],
    };

    private readonly Dictionary<HotkeyAction, HotkeyBinding[]> _bindings;

    // parse failures are collected rather than thrown: one bad string must not stop Hydra starting, and
    // the action keeps its default so the user is never left with no way to trigger it.
    public IReadOnlyList<string> Errors { get; }

    private HotkeyBindings(Dictionary<HotkeyAction, HotkeyBinding[]> bindings, List<string> errors)
    {
        _bindings = bindings;
        Errors = errors;
    }

    public static HotkeyBindings Build(Dictionary<string, List<string>>? configured)
    {
        var errors = new List<string>();
        var bindings = new Dictionary<HotkeyAction, HotkeyBinding[]>();

        foreach (var (action, defaultSpecs) in Defaults)
        {
            var parsed = Parse(FindConfigured(configured, action, errors) ?? defaultSpecs.AsEnumerable(), action, errors);
            // every action keeps a working chord even when all of its configured strings were rejected
            bindings[action] = parsed.Length > 0 ? parsed : Parse(defaultSpecs, action, null);
        }

        if (configured != null)
        {
            foreach (var name in configured.Keys)
            {
                if (!Enum.TryParse<HotkeyAction>(name, ignoreCase: true, out _))
                    errors.Add($"unknown hotkey action '{name}'; ignored");
            }
        }

        return new HotkeyBindings(bindings, errors);
    }

    private static HotkeyBinding[] Parse(IEnumerable<string> specs, HotkeyAction action, List<string>? errors)
    {
        var parsed = new List<HotkeyBinding>();
        foreach (var spec in specs)
        {
            if (HotkeyBinding.TryParse(spec, out var binding, out var error))
                parsed.Add(binding!);
            else
                errors?.Add($"hotkey '{spec}' for {action} {error}; ignored");
        }
        return [.. parsed];
    }

    private static List<string>? FindConfigured(Dictionary<string, List<string>>? configured, HotkeyAction action, List<string> errors)
    {
        if (configured == null) return null;
        foreach (var (name, specs) in configured)
        {
            if (!name.EqualsIgnoreCase(action.ToString())) continue;
            if (specs.Count != 0) return specs;
            errors.Add($"hotkey list for {action} is empty; keeping the default");
            return null;
        }
        return null;
    }

    public HotkeyAction? Match(KeyEvent ev)
    {
        foreach (var (action, bindings) in _bindings)
        {
            foreach (var binding in bindings)
            {
                if (binding.Matches(ev)) return action;
            }
        }
        return null;
    }
}

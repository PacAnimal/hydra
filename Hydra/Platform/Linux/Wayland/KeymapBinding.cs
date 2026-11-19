using System.Globalization;
using System.Text.RegularExpressions;

namespace Hydra.Platform.Linux.Wayland;

// a keymap (XKB_KEYMAP_FORMAT_TEXT_V1, as xkb_keymap_get_as_string writes it) with one spare key bound to a keysym,
// as wtype types a character its layout lacks: the key's own definition in xkb_symbols replaced, or one appended, its
// only level the keysym on both of ALPHABETIC's, so neither Shift nor CapsLock changes what it types
internal static partial class KeymapBinding
{
    // the name given a keycode the keycodes section leaves unnamed
    private const string OwnName = "HYDR";

    // the keymap with the evdev key typing the keysym at its first level, checked with xkbcommon: it compiles, the
    // key types the keysym and changes no modifier. null when the text has no sections to rewrite or the check fails
    internal static string? Bind(string keymapText, uint evdevKey, uint keysym)
    {
        if (Rewrite(keymapText, evdevKey + 8, keysym) is not { } text) return null;
        try
        {
            using var keymap = XkbKeymap.FromText(text);
            using var lookup = new XkbKeymapLookup(keymap);
            if (lookup.BaseKeysym(evdevKey) != keysym) return null;
            using var modifiers = new XkbModifierState(keymap);
            return modifiers.Update(evdevKey, true) ? null : text;
        }
        catch (InvalidOperationException)
        {
            return null;
        }
    }

    // the text rewrite alone: the xkb keycode's symbols set to the keysym, its name added to the keycodes section
    // when it has none there. null when the text lacks either section or the keycode is outside its range
    internal static string? Rewrite(string keymapText, uint xkbKeycode, uint keysym)
    {
        if (Section(keymapText, "xkb_keycodes") is not { } keycodes) return null;
        var names = keymapText.Substring(keycodes.Start, keycodes.Length);
        var max = MaximumPattern().Match(names);
        if (!max.Success || xkbKeycode > uint.Parse(max.Groups[1].Value, CultureInfo.InvariantCulture)) return null;

        var name = KeyName(names, xkbKeycode);
        var text = keymapText;
        if (name == null)
        {
            if (names.Contains($"<{OwnName}>", StringComparison.Ordinal)) return null;
            name = OwnName;
            var at = keycodes.Start + max.Index + max.Length;
            text = text.Insert(at, $"\n\t<{name}> = {xkbKeycode};");
        }

        if (Section(text, "xkb_symbols") is not { } symbols) return null;
        var body = text.Substring(symbols.Start, symbols.Length);
        var sym = $"0x{keysym:x}";
        var definition = $"\tkey <{name}> {{\n\t\ttype= \"ALPHABETIC\",\n\t\tsymbols[1]= [ {sym}, {sym} ]\n\t}};\n";
        var existing = new Regex($@"^\tkey <{Regex.Escape(name)}> \{{.*?\}};\n", RegexOptions.Multiline | RegexOptions.Singleline).Match(body);
        body = existing.Success
            ? body.Remove(existing.Index, existing.Length).Insert(existing.Index, definition)
            : body + definition;
        return text[..symbols.Start] + body + text[(symbols.Start + symbols.Length)..];
    }

    // the name the keycodes section gives the keycode, never an alias's
    private static string? KeyName(string keycodes, uint xkbKeycode)
    {
        foreach (Match m in KeycodePattern().Matches(keycodes))
            if (uint.Parse(m.Groups[2].Value, CultureInfo.InvariantCulture) == xkbKeycode) return m.Groups[1].Value;
        return null;
    }

    // a section's body: from the line after its opening to the start of its closing "};" line
    private static TextSpan? Section(string text, string kind)
    {
        var open = new Regex($@"^{kind}\b[^{{\n]*\{{\n", RegexOptions.Multiline).Match(text);
        if (!open.Success) return null;
        var start = open.Index + open.Length;
        var close = text.IndexOf("\n};", start - 1, StringComparison.Ordinal);
        if (close < 0) return null;
        return new TextSpan(start, close + 1 - start);
    }

    private readonly record struct TextSpan(int Start, int Length);

    [GeneratedRegex(@"^\s*<([^>\s]+)>\s*=\s*(\d+)\s*;", RegexOptions.Multiline)]
    private static partial Regex KeycodePattern();

    [GeneratedRegex(@"^\s*maximum\s*=\s*(\d+)\s*;", RegexOptions.Multiline)]
    private static partial Regex MaximumPattern();
}

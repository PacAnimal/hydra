using System.Text;

namespace Hydra.Keyboard;

// shared keyboard resolution utilities used by multiple platform resolvers.
internal static class KeyResolver
{
    // classifies a unicode char from platform key translation output as a printable char or SpecialKey.
    // control characters are mapped to their SpecialKey constants; other printable chars pass through.
    internal static (char? ch, SpecialKey? key) ClassifyChar(char c) => c switch
    {
        (char)3 => (null, SpecialKey.KP_Enter),
        (char)8 => (null, SpecialKey.BackSpace),
        (char)9 => (null, SpecialKey.Tab),
        (char)13 => (null, SpecialKey.Return),
        (char)27 => (null, SpecialKey.Escape),
        (char)127 => (null, SpecialKey.Delete),
        _ when c < 32 => (null, null),
        _ => (c, null),
    };

    // maps spacing accent characters (the standalone form of a dead key) to their Unicode combining equivalents.
    // used by both Windows (ToUnicodeEx returns spacing form directly) and Linux (via DeadKeySpacing lookup).
    internal static readonly Dictionary<char, char> SpacingToCombining = new()
    {
        { '\u0060', '\u0300' },  // ` grave accent
        { '\u00B4', '\u0301' },  // ´ acute accent
        { '\u005E', '\u0302' },  // ^ circumflex
        { '\u007E', '\u0303' },  // ~ tilde
        { '\u00AF', '\u0304' },  // ¯ macron
        { '\u02D8', '\u0306' },  // ˘ breve
        { '\u02D9', '\u0307' },  // ˙ dot above
        { '\u00A8', '\u0308' },  // ¨ diaeresis
        { '\u02DA', '\u030A' },  // ˚ ring above
        { '\u02DD', '\u030B' },  // ˝ double acute
        { '\u02C7', '\u030C' },  // ˇ caron
        { '\u00B8', '\u0327' },  // ¸ cedilla
        { '\u02DB', '\u0328' },  // ˛ ogonek
        { '\u002F', '\u0335' },  // / stroke (XK_dead_stroke)
    };

    // compose a pending dead key combining character with a base character via NFC normalization.
    // if composition produces no single codepoint (incompatible pair), returns the base unchanged.
    internal static char Compose(char baseChar, char combiningChar)
    {
        var composed = new string([baseChar, combiningChar]).Normalize(NormalizationForm.FormC);
        return composed.Length == 1 ? composed[0] : baseChar;
    }

    // apply dead key resolution: space + pending → spacing form; otherwise compose.
    // on incompatible pair, returns base char unchanged (dead key silently consumed).
    internal static char ComposeOrSpacing(char baseChar, char combining, char spacing) =>
        baseChar == ' ' && spacing != '\0' ? spacing : Compose(baseChar, combining);

    // replay the key event that was emitted on key-down, using the stored char/special from _keyDownId.
    // shared across all three platform resolvers (mac, xorg, win) — TKey is int or uint depending on platform.
    internal static KeyEvent? ReplayKeyUp<TKey>(Dictionary<TKey, (char? ch, SpecialKey? key)> keyDownId, TKey key, KeyModifiers mods)
        where TKey : notnull
    {
        keyDownId.Remove(key, out var downVal);
        if (downVal.ch.HasValue) return KeyEvent.Char(KeyEventType.KeyUp, downVal.ch.Value, mods);
        if (downVal.key.HasValue) return KeyEvent.Special(KeyEventType.KeyUp, downVal.key.Value, mods);
        return null;
    }
}

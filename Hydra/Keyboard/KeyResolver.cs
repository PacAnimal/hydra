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

    // compose a pending dead key combining character with a base character via NFC normalization.
    // if composition produces no single codepoint (incompatible pair), returns the base unchanged.
    internal static char Compose(char baseChar, char combiningChar)
    {
        var composed = new string([baseChar, combiningChar]).Normalize(NormalizationForm.FormC);
        return composed.Length == 1 ? composed[0] : baseChar;
    }
}

using Hydra.Keyboard;

namespace Hydra.Platform.Linux;

// the X11 keysym a key or character is spelled with
internal static class XorgKeysym
{
    // every key Hydra can inject on X11 is in the map; 0 means "no keysym", which callers treat as unsupported
    internal static ulong Of(SpecialKey key) => XorgSpecialKeyMap.Instance.Reverse.TryGetValue(key, out var keysym) ? keysym : 0;

    // Latin-1 keysyms are the character itself; any other is the Unicode extension, 0x01000000 | codepoint
    internal static ulong Of(char ch) => ch <= '\xFF' ? ch : 0x01000000u | ch;
}

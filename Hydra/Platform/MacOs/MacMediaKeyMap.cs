using Hydra.Keyboard;

namespace Hydra.Platform.MacOs;

// media keys travel as NX_SYSDEFINED events carrying an NX_KEYTYPE_* (ev_keymap.h), not as key codes
internal static class MacMediaKeyMap
{
    // a media key's PhysicalKey, clear of every keycode (keycodes are 16-bit)
    internal static int PhysicalKey(uint nxKeyType) => (int)nxKeyType | 0x10000;

    internal static readonly IReadOnlyDictionary<SpecialKey, uint> OutputKeys = new Dictionary<SpecialKey, uint>
    {
        [SpecialKey.AudioVolumeUp] = NativeMethods.NXKeytypeSoundUp,
        [SpecialKey.AudioVolumeDown] = NativeMethods.NXKeytypeSoundDown,
        [SpecialKey.AudioMute] = NativeMethods.NXKeytypeMute,
        [SpecialKey.AudioPlay] = NativeMethods.NXKeytypePlay,
        [SpecialKey.AudioNext] = NativeMethods.NXKeytypeNext,
        [SpecialKey.AudioPrev] = NativeMethods.NXKeytypePrevious,
        [SpecialKey.BrightnessUp] = NativeMethods.NXKeytypeBrightnessUp,
        [SpecialKey.BrightnessDown] = NativeMethods.NXKeytypeBrightnessDown,
        [SpecialKey.Eject] = NativeMethods.NXKeytypeEject,
    };

    // some keyboards send fast-forward and rewind for next and previous; captured, never injected
    private static readonly Dictionary<uint, SpecialKey> InputKeys = new(OutputKeys.ToDictionary(p => p.Value, p => p.Key))
    {
        [NativeMethods.NXKeytypeFast] = SpecialKey.AudioNext,
        [NativeMethods.NXKeytypeRewind] = SpecialKey.AudioPrev,
    };

    internal static bool TryGetNxKeyType(SpecialKey key, out uint nxType) => OutputKeys.TryGetValue(key, out nxType);

    internal static bool TryGetKey(uint nxType, out SpecialKey key) => InputKeys.TryGetValue(nxType, out key);

    // an NX_SYSDEFINED event's data1: the type in bits 16-31, state 0xA (down) or 0xB (up) in bits 8-15, repeat in bit 0
    internal static NxKey Decode(long data1) => new((uint)((data1 >> 16) & 0xFFFF), (data1 & 0x100) == 0, (data1 & 0x1) != 0);

    internal readonly record struct NxKey(uint KeyType, bool IsDown, bool IsRepeat);
}

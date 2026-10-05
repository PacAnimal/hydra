using Hydra.Keyboard;

namespace Hydra.Platform.MacOs;

// media keys travel as NX_SYSDEFINED events carrying an NX_KEYTYPE_* (ev_keymap.h), not as key codes
internal static class MacMediaKeyMap
{
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
}

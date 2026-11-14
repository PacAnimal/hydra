namespace Hydra.Platform.Linux;

// a key or button change made up after a SYN_DROPPED, so the held state matches the kernel's again
internal sealed record EvdevKeyChange(ushort Code, bool Down);

// what the event loop does with one event read from a device
internal enum EvdevSyncAction
{
    // handle it as usual
    Pass,
    // lost to a buffer overflow: ignore it
    Discard,
    // the overflow is over: query the key state and make up what was missed
    Resync,
}

// The kernel's recovery from an overflowed evdev buffer (Documentation/input/event-codes.rst), as libevdev does it:
// after SYN_DROPPED every event up to and including the next SYN_REPORT is dropped, then the keys down are read
// with EVIOCGKEY and compared with what the reader thinks is down. Relative motion lost in the gap stays lost.
internal sealed class EvdevResync
{
    // bytes in an EVIOCGKEY bitmask: KEY_MAX+1 bits
    internal const int KeyBytes = EvdevNativeMethods.KEY_MAX / 8 + 1;

    // between a SYN_DROPPED and the SYN_REPORT that ends it
    public bool Dropping { get; private set; }

    public EvdevSyncAction Next(ushort type, ushort code)
    {
        if (type == EvdevNativeMethods.EV_SYN && code == EvdevNativeMethods.SYN_DROPPED)
        {
            Dropping = true;
            return EvdevSyncAction.Discard;
        }
        if (!Dropping) return EvdevSyncAction.Pass;
        if (type != EvdevNativeMethods.EV_SYN || code != EvdevNativeMethods.SYN_REPORT) return EvdevSyncAction.Discard;
        Dropping = false;
        return EvdevSyncAction.Resync;
    }

    // releases for codes held here but up in the kernel, then presses for codes down there but not held here,
    // each in code order
    internal static List<EvdevKeyChange> Diff(IReadOnlySet<ushort> held, byte[] keys)
    {
        var changes = new List<EvdevKeyChange>();
        foreach (var code in held.Order())
            if (!IsDown(keys, code)) changes.Add(new EvdevKeyChange(code, false));
        for (var code = 0; code < keys.Length * 8 && code <= EvdevNativeMethods.KEY_MAX; code++)
            if (IsDown(keys, code) && !held.Contains((ushort)code)) changes.Add(new EvdevKeyChange((ushort)code, true));
        return changes;
    }

    // whether a read key event repeats what is already held: EVIOCGKEY's state includes changes whose events are
    // still queued behind the SYN_REPORT, so after a resync those arrive a second time. A repeat (2) is never stale
    internal static bool IsStale(IReadOnlySet<ushort> held, ushort code, int value) => value switch
    {
        1 => held.Contains(code),
        0 => !held.Contains(code),
        _ => false,
    };

    private static bool IsDown(byte[] keys, int code) => code / 8 < keys.Length && (keys[code / 8] & (1 << (code % 8))) != 0;
}

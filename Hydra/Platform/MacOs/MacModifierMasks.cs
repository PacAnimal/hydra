namespace Hydra.Platform.MacOs;

// the CGEventFlags bits each modifier key owns: its generic flag and the device-dependent NX bits
// (in the low word of CGEventFlags) for its own side and the other side.
internal static class MacModifierMasks
{
    private static readonly ModifierMasks Shift =
        new(NativeMethods.KCGEventFlagMaskShift, NativeMethods.NxDeviceLShiftKeyMask, NativeMethods.NxDeviceRShiftKeyMask);
    private static readonly ModifierMasks Control =
        new(NativeMethods.KCGEventFlagMaskControl, NativeMethods.NxDeviceLCtlKeyMask, NativeMethods.NxDeviceRCtlKeyMask);
    private static readonly ModifierMasks Option =
        new(NativeMethods.KCGEventFlagMaskAlternate, NativeMethods.NxDeviceLAltKeyMask, NativeMethods.NxDeviceRAltKeyMask);
    private static readonly ModifierMasks Command =
        new(NativeMethods.KCGEventFlagMaskCommand, NativeMethods.NxDeviceLCmdKeyMask, NativeMethods.NxDeviceRCmdKeyMask);
    private static readonly ModifierMasks[] Sided = [Shift, Control, Option, Command];

    // masks for a modifier vk; all zero for a key that is not a modifier
    internal static ModifierMasks Of(ulong vk) => vk switch
    {
        MacVirtualKey.Shift => Shift,
        MacVirtualKey.RightShift => Shift.Mirrored,
        MacVirtualKey.Control => Control,
        MacVirtualKey.RightControl => Control.Mirrored,
        MacVirtualKey.Option => Option,
        MacVirtualKey.RightOption => Option.Mirrored,
        MacVirtualKey.Command => Command,
        MacVirtualKey.RightCommand => Command.Mirrored,
        MacVirtualKey.CapsLock => new ModifierMasks(NativeMethods.KCGEventFlagMaskAlphaShift, 0, 0),
        _ => default,
    };

    // derives generic CGEventFlag bits from device-dependent NX masks,
    // so releasing one of two held L/R modifiers keeps the generic flag set.
    internal static ulong GenericFlags(uint deviceFlags)
    {
        ulong flags = 0;
        foreach (var m in Sided)
            if ((deviceFlags & (m.Device | m.OtherDevice)) != 0) flags |= m.Generic;
        return flags;
    }
}

internal readonly record struct ModifierMasks(ulong Generic, uint Device, uint OtherDevice)
{
    internal ModifierMasks Mirrored => this with { Device = OtherDevice, OtherDevice = Device };
}

namespace Hydra.Platform.Linux.Wayland;

// which of an EIS context's devices serve what: injection only through a resumed one, the screens from any added one
internal static class EiDevices
{
    // the first resumed device with the capability, the pointer preferred for buttons and scroll
    internal static EiDevice? Pick(IReadOnlyList<EiDevice> devices, EiCapabilities capability)
    {
        if (capability is EiCapabilities.Button or EiCapabilities.Scroll)
        {
            var pointer = Pick(devices, EiCapabilities.Pointer);
            if (pointer != null && pointer.Has(capability)) return pointer;
        }
        // indexed: this runs per injected event and an interface foreach allocates
        for (var i = 0; i < devices.Count; i++)
            if (devices[i].IsResumed && devices[i].Has(capability)) return devices[i];
        return null;
    }

    // a resumed keyboard and a resumed pointer of either kind
    internal static bool CanTypeAndPoint(IReadOnlyList<EiDevice> devices) =>
        Pick(devices, EiCapabilities.Keyboard) != null
        && (Pick(devices, EiCapabilities.Pointer) != null || Pick(devices, EiCapabilities.PointerAbsolute) != null);

    // the regions of the absolute pointer injection uses, else of the first added one: the compositor lays them out at
    // device-added and may resume the device much later, or never; null with no absolute pointer
    internal static IReadOnlyList<EiRegion>? AbsoluteRegions(IReadOnlyList<EiDevice> devices)
    {
        if (Pick(devices, EiCapabilities.PointerAbsolute) is { } resumed) return resumed.Regions;
        for (var i = 0; i < devices.Count; i++)
            if (devices[i].Has(EiCapabilities.PointerAbsolute)) return devices[i].Regions;
        return null;
    }
}

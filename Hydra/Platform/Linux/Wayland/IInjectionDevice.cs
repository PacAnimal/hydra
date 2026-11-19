using Hydra.Screen;

namespace Hydra.Platform.Linux.Wayland;

// what a Wayland slave injects through: an EIS sender on the RemoteDesktop portal (EiInjectionDevice), or the wlroots
// virtual pointer and keyboard where the portal has no RemoteDesktop (VirtualInjectionDevice). Each Send-style call
// says whether it sent anything, and Frame closes what was sent. The session owns it; the output handler only uses it.
internal interface IInjectionDevice : IDisposable
{
    // for the log
    string Name { get; }

    // the XKB keymap (XKB_KEYMAP_FORMAT_TEXT_V1) keys are typed on; null when the device has none
    string? Keymap { get; }

    // where an absolute move may land, in logical pixels; null when the device cannot move absolutely
    IReadOnlyList<ScreenBounds>? AbsoluteRegions { get; }

    // the locked modifiers of the keymap as last known; null until known
    uint? LockedModifiers { get; }

    // the effective layout group (xkb layout index) as last known; 0 until known
    uint Group { get; }

    // whether SelectGroup takes: an EIS client cannot change the compositor's group
    bool SelectsGroup { get; }

    bool IsLost { get; }

    event Action<uint>? LockedModifiersChanged;

    // the layout group keys go in under until called with null, which gives the device's own back; false when nothing
    // was sent
    bool SelectGroup(uint? group);

    // the connection behind the device is gone; raised once
    event Action? Lost;

    void StartEmulating();

    void StopEmulating();

    // an evdev key code (KEY_*)
    bool Key(uint evdevKey, bool pressed);

    // an evdev key that types the keysym, on a keymap the device uploads for it until another key goes in; null when
    // the device cannot upload a keymap (EIS) or the keysym cannot be bound
    uint? BindKeysym(uint keysym);

    // an evdev button code (BTN_*)
    bool Button(uint evdevButton, bool pressed);

    bool MotionAbsolute(double x, double y);

    bool Motion(double dx, double dy);

    // 120 a detent (less for a high-resolution wheel), positive down and right
    bool Scroll(int dx, int dy);

    void Frame();
}

internal enum InjectionBackend
{
    None,
    Eis,
    VirtualInput,
}

internal static class InjectionBackends
{
    // RemoteDesktop's EIS where the portal has it, as on GNOME and KDE; the wlroots virtual input protocols where the
    // compositor has them instead (Hyprland, Sway), asked only then since it opens a Wayland connection
    internal static InjectionBackend Choose(bool hasRemoteDesktop, Func<bool> hasVirtualInput)
    {
        if (hasRemoteDesktop) return InjectionBackend.Eis;
        return hasVirtualInput() ? InjectionBackend.VirtualInput : InjectionBackend.None;
    }
}

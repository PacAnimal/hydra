namespace Hydra.Platform.Linux.Wayland;

// The one portal session a Wayland run holds, shared by everything that needs it: a master's InputCapture session and
// the EIS receiver it hands out, or a slave's injection device: its RemoteDesktop session and EIS sender, or where the
// portal has no RemoteDesktop (Hyprland), the wlroots virtual pointer and keyboard. The screen detector starts
// before the input handler and needs the same session (the compositor's monitors come from it), so neither owns it:
// whichever asks first starts it. A session the portal closes, or an EIS or Wayland connection that drops, is reported once
// through SessionLost; the next call starts a fresh one, and its start raises ScreensChanged.
internal interface IWaylandSession : IAsyncDisposable
{
    // master: the InputCapture session
    Task<InputCapturePortal> InputCapture(CancellationToken cancel);

    // master: the EIS receiver connected to the InputCapture session
    Task<EiContext> Receiver(CancellationToken cancel);

    // slave: what input is injected through, ready to type and point (an EIS sender's devices resumed); the session
    // owns it, and a fresh start brings a new one
    Task<IInjectionDevice> Injection(CancellationToken cancel);

    // the portal clipboard on this run's session; null when the portal offers none or did not enable it
    Task<PortalClipboard?> Clipboard(CancellationToken cancel);

    // master: the GlobalShortcuts session beside the InputCapture one, for the hotkeys at home, where no keys reach EIS;
    // null when the portal has no GlobalShortcuts or its session could not start. It goes with the session it belongs to,
    // and one the portal closes is replaced on the next call
    Task<GlobalShortcutsPortal?> Shortcuts(CancellationToken cancel);

    // the monitors as the compositor lays them out, in logical pixels: InputCapture's zones on a master, the
    // absolute pointer's regions on an EIS slave, the outputs' logical layout on a wlroots one
    Task<IReadOnlyList<DetectedScreen>> Screens(CancellationToken cancel);

    // master: InputCapture's zones with the zone set's serial, which SetPointerBarriers must name
    Task<PortalZoneSet> Zones(CancellationToken cancel);

    // the screens may have moved, or a fresh session started
    event Action? ScreensChanged;

    event Action? SessionLost;
}

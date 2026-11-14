using System.Runtime.CompilerServices;

namespace Hydra.Platform.Linux.Wayland;

// What the master's input handler takes from the portal session, as interfaces so a test can stand in for the portal
// and the EIS receiver. WaylandCaptureSession serves them from the run's IWaylandSession, one wrapper per portal object
internal interface ICaptureSession
{
    Task<ICapturePortal> InputCapture(CancellationToken cancel);
    Task<ICaptureReceiver> Receiver(CancellationToken cancel);
    Task<GlobalShortcutsPortal?> Shortcuts(CancellationToken cancel);
    Task<PortalZoneSet> Zones(CancellationToken cancel);
    event Action? ScreensChanged;
    event Action? SessionLost;
}

// InputCapturePortal's part the handler uses
internal interface ICapturePortal
{
    event Action<uint, double, double, uint>? Activated;
    event Action<uint>? Deactivated;
    event Action? Disabled;
    event Action? ZonesChanged;
    Task<IReadOnlyList<uint>> SetPointerBarriers(IReadOnlyList<PortalBarrier> barriers, uint zoneSet, CancellationToken cancel);
    Task Enable();
    Task Disable();
    Task Release(uint activationId, double? cursorX, double? cursorY);
}

// the EIS receiver's part the handler uses
internal interface ICaptureReceiver
{
    event Action<string?>? KeyboardKeymapChanged;
    event Action<uint, bool>? KeyboardKey;
    event Action<double, double>? PointerMotion;
    event Action<uint, bool>? Button;
    event Action<double, double>? ScrollDelta;
    event Action<int, int>? ScrollDiscrete;
    event Action<bool, bool>? ScrollStop;
    event Action? Frame;
    string? KeyboardKeymap { get; }
    bool IsDisconnected { get; }
}

internal sealed class WaylandCaptureSession(IWaylandSession session) : ICaptureSession
{
    // the same portal object is always the same wrapper, which the handler compares to tell a session from a fresh one
    private readonly ConditionalWeakTable<InputCapturePortal, PortalCapture> _portals = [];
    private readonly ConditionalWeakTable<EiContext, EisCapture> _receivers = [];

    public async Task<ICapturePortal> InputCapture(CancellationToken cancel) =>
        _portals.GetValue(await session.InputCapture(cancel), portal => new PortalCapture(portal));

    public async Task<ICaptureReceiver> Receiver(CancellationToken cancel) =>
        _receivers.GetValue(await session.Receiver(cancel), receiver => new EisCapture(receiver));

    public Task<GlobalShortcutsPortal?> Shortcuts(CancellationToken cancel) => session.Shortcuts(cancel);

    public Task<PortalZoneSet> Zones(CancellationToken cancel) => session.Zones(cancel);

    public event Action? ScreensChanged
    {
        add => session.ScreensChanged += value;
        remove => session.ScreensChanged -= value;
    }

    public event Action? SessionLost
    {
        add => session.SessionLost += value;
        remove => session.SessionLost -= value;
    }

    private sealed class PortalCapture(InputCapturePortal portal) : ICapturePortal
    {
        public event Action<uint, double, double, uint>? Activated
        {
            add => portal.Activated += value;
            remove => portal.Activated -= value;
        }

        public event Action<uint>? Deactivated
        {
            add => portal.Deactivated += value;
            remove => portal.Deactivated -= value;
        }

        public event Action? Disabled
        {
            add => portal.Disabled += value;
            remove => portal.Disabled -= value;
        }

        public event Action? ZonesChanged
        {
            add => portal.ZonesChanged += value;
            remove => portal.ZonesChanged -= value;
        }

        public Task<IReadOnlyList<uint>> SetPointerBarriers(IReadOnlyList<PortalBarrier> barriers, uint zoneSet, CancellationToken cancel) =>
            portal.SetPointerBarriers(barriers, zoneSet, cancel);

        public Task Enable() => portal.Enable();
        public Task Disable() => portal.Disable();
        public Task Release(uint activationId, double? cursorX, double? cursorY) => portal.Release(activationId, cursorX, cursorY);
    }

    private sealed class EisCapture(EiContext receiver) : ICaptureReceiver
    {
        public event Action<string?>? KeyboardKeymapChanged
        {
            add => receiver.KeyboardKeymapChanged += value;
            remove => receiver.KeyboardKeymapChanged -= value;
        }

        public event Action<uint, bool>? KeyboardKey
        {
            add => receiver.KeyboardKey += value;
            remove => receiver.KeyboardKey -= value;
        }

        public event Action<double, double>? PointerMotion
        {
            add => receiver.PointerMotion += value;
            remove => receiver.PointerMotion -= value;
        }

        public event Action<uint, bool>? Button
        {
            add => receiver.Button += value;
            remove => receiver.Button -= value;
        }

        public event Action<double, double>? ScrollDelta
        {
            add => receiver.ScrollDelta += value;
            remove => receiver.ScrollDelta -= value;
        }

        public event Action<int, int>? ScrollDiscrete
        {
            add => receiver.ScrollDiscrete += value;
            remove => receiver.ScrollDiscrete -= value;
        }

        public event Action<bool, bool>? ScrollStop
        {
            add => receiver.ScrollStop += value;
            remove => receiver.ScrollStop -= value;
        }

        public event Action? Frame
        {
            add => receiver.Frame += value;
            remove => receiver.Frame -= value;
        }

        public string? KeyboardKeymap => receiver.KeyboardKeymap;
        public bool IsDisconnected => receiver.IsDisconnected;
    }
}

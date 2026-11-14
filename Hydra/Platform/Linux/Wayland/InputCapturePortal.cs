using System.Runtime.InteropServices;
using Microsoft.Extensions.Logging;
using Tmds.DBus.Protocol;

namespace Hydra.Platform.Linux.Wayland;

internal sealed record PortalZone(int X, int Y, uint Width, uint Height);

internal sealed record PortalZoneSet(uint Serial, IReadOnlyList<PortalZone> Zones);

internal sealed record PortalBarrier(uint Id, int X1, int Y1, int X2, int Y2);

// An org.freedesktop.portal.InputCapture session: the compositor captures input once the pointer crosses a barrier and
// sends it over EIS. Version 2 portals create the session, take the clipboard and start it in separate steps, so the
// user's grant can persist; version 1 starts the session as it is created, with neither.
internal sealed class InputCapturePortal : IAsyncDisposable
{
    private const string Interface = "org.freedesktop.portal.InputCapture";
    private const uint Capabilities = 1 | 2;   // KEYBOARD | POINTER

    private readonly PortalBus _bus;
    private SessionWatch? _watch;

    private InputCapturePortal(PortalBus bus, ObjectPath session)
    {
        _bus = bus;
        Session = session;
    }

    internal ObjectPath Session { get; }
    internal string? RestoreToken { get; private set; }
    internal bool ClipboardEnabled { get; private set; }
    // whether the portal could have enabled the clipboard: never on version 1
    internal bool ClipboardSupported { get; private set; }

    // activation id, cursor position in zone coordinates, and the barrier crossed (0 when the compositor cannot tell)
    internal event Action<uint, double, double, uint>? Activated;
    internal event Action<uint>? Deactivated;
    internal event Action? Disabled;
    internal event Action? ZonesChanged;
    internal event Action? Closed;

    internal static async Task<InputCapturePortal> Start(PortalBus bus, string? restoreToken, bool wantClipboard, ILogger log, CancellationToken cancel)
    {
        uint version;
        try
        {
            version = await bus.Version(Interface);
        }
        catch (DBusErrorReplyException e)
        {
            throw new PortalException("The desktop portal has no InputCapture interface", e);
        }
        if (log.IsEnabled(LogLevel.Debug)) log.LogDebug("InputCapture portal version {Version}", version);
        var portal = version >= 2 ? await CreateSession2(bus) : await CreateSession(bus, cancel);
        try
        {
            await portal.Watch();
            if (version >= 2) await portal.StartSession(restoreToken, wantClipboard, log, cancel);
            return portal;
        }
        catch
        {
            await portal.DisposeAsync();
            throw;
        }
    }

    private static async Task<InputCapturePortal> CreateSession2(PortalBus bus)
    {
        var options = new Dictionary<string, VariantValue> { ["session_handle_token"] = bus.NextToken() };
        var call = bus.Message(PortalBus.DesktopPath, Interface, "CreateSession2", "a{sv}", (ref MessageWriter writer) => writer.WriteDictionary(options));
        var results = await bus.Connection.CallMethodAsync(call, (message, _) => message.GetBodyReader().ReadDictionaryOfStringToVariantValue());
        return new InputCapturePortal(bus, PortalBus.SessionHandle(results));
    }

    // the deprecated version 1 call, whose session is started as it is created
    private static async Task<InputCapturePortal> CreateSession(PortalBus bus, CancellationToken cancel)
    {
        var sessionToken = bus.NextToken();
        var response = await bus.Request(token => bus.Message(PortalBus.DesktopPath, Interface, "CreateSession", "sa{sv}", (ref MessageWriter writer) =>
        {
            writer.WriteString("");
            writer.WriteDictionary(new Dictionary<string, VariantValue>
            {
                ["handle_token"] = token,
                ["session_handle_token"] = sessionToken,
                ["capabilities"] = Capabilities,
            });
        }), cancel);
        return new InputCapturePortal(bus, PortalBus.SessionHandle(response.Check("Creating the input capture session").Results));
    }

    private async Task StartSession(string? restoreToken, bool wantClipboard, ILogger log, CancellationToken cancel)
    {
        ClipboardSupported = await _bus.Has(PortalBus.ClipboardInterface);
        if (wantClipboard) await PortalClipboard.RequestClipboard(_bus, Session);

        var response = await _bus.Request(token =>
        {
            var options = new Dictionary<string, VariantValue>
            {
                ["handle_token"] = token,
                ["capabilities"] = Capabilities,
                ["persist_mode"] = PortalBus.PersistUntilRevoked,
            };
            if (restoreToken != null) options["restore_token"] = restoreToken;
            return _bus.Message(PortalBus.DesktopPath, Interface, "Start", "osa{sv}", (ref MessageWriter writer) =>
            {
                writer.WriteObjectPath(Session);
                writer.WriteString("");
                writer.WriteDictionary(options);
            });
        }, cancel);
        response.Check("Starting the input capture session");
        RestoreToken = response.String("restore_token");
        ClipboardEnabled = response.Bool("clipboard_enabled");
        PortalBus.LogStarted(log, "Input capture", Session, ClipboardEnabled, RestoreToken);
    }

    private async Task Watch()
    {
        var watch = _watch = await _bus.WatchSession(Session, () => Closed?.Invoke());
        watch.Add(await _bus.WatchSessionSignal(Interface, "Activated", Session, options =>
        {
            var id = options.TryGetValue("activation_id", out var a) ? a.GetUInt32() : 0;
            double x = 0, y = 0;
            if (options.TryGetValue("cursor_position", out var position))
            {
                x = position.GetItem(0).GetDouble();
                y = position.GetItem(1).GetDouble();
            }
            var barrier = options.TryGetValue("barrier_id", out var b) ? b.GetUInt32() : 0;
            Activated?.Invoke(id, x, y, barrier);
        }));
        watch.Add(await _bus.WatchSessionSignal(Interface, "Deactivated", Session, options =>
            Deactivated?.Invoke(options.TryGetValue("activation_id", out var a) ? a.GetUInt32() : 0)));
        watch.Add(await _bus.WatchSessionSignal(Interface, "Disabled", Session, _ => Disabled?.Invoke()));
        watch.Add(await _bus.WatchSessionSignal(Interface, "ZonesChanged", Session, _ => ZonesChanged?.Invoke()));
    }

    internal async Task<PortalZoneSet> GetZones(CancellationToken cancel)
    {
        var response = await _bus.Request(token =>
            _bus.SessionCall(Interface, "GetZones", Session, new Dictionary<string, VariantValue> { ["handle_token"] = token }), cancel);
        response.Check("Getting the input capture zones");
        var zones = new List<PortalZone>();
        if (response.Results.TryGetValue("zones", out var array))
        {
            // each zone is (width, height, x, y)
            for (var i = 0; i < array.Count; i++)
            {
                var zone = array.GetItem(i);
                zones.Add(new PortalZone(zone.GetItem(2).GetInt32(), zone.GetItem(3).GetInt32(), zone.GetItem(0).GetUInt32(), zone.GetItem(1).GetUInt32()));
            }
        }
        var serial = response.Results.TryGetValue("zone_set", out var set) ? set.GetUInt32() : 0;
        return new PortalZoneSet(serial, zones);
    }

    // the ids of the barriers the compositor refused
    internal async Task<IReadOnlyList<uint>> SetPointerBarriers(IReadOnlyList<PortalBarrier> barriers, uint zoneSet, CancellationToken cancel)
    {
        var response = await _bus.Request(token => _bus.Message(PortalBus.DesktopPath, Interface, "SetPointerBarriers", "oa{sv}aa{sv}u", (ref MessageWriter writer) =>
        {
            writer.WriteObjectPath(Session);
            writer.WriteDictionary(new Dictionary<string, VariantValue> { ["handle_token"] = token });
            var array = writer.WriteArrayStart(DBusType.Array);
            foreach (var barrier in barriers)
            {
                writer.WriteDictionary(new Dictionary<string, VariantValue>
                {
                    ["barrier_id"] = barrier.Id,
                    ["position"] = VariantValue.Struct(barrier.X1, barrier.Y1, barrier.X2, barrier.Y2),
                });
            }
            writer.WriteArrayEnd(array);
            writer.WriteUInt32(zoneSet);
        }), cancel);
        response.Check("Setting the pointer barriers");
        return response.Results.TryGetValue("failed_barriers", out var failed) ? failed.GetArray<uint>() : [];
    }

    internal Task Enable() => _bus.Connection.CallMethodAsync(_bus.SessionCall(Interface, "Enable", Session, []));

    internal Task Disable() => _bus.Connection.CallMethodAsync(_bus.SessionCall(Interface, "Disable", Session, []));

    internal Task Release(uint activationId, double? cursorX, double? cursorY)
    {
        var options = new Dictionary<string, VariantValue> { ["activation_id"] = activationId };
        if (cursorX is { } x && cursorY is { } y) options["cursor_position"] = VariantValue.Struct(x, y);
        return _bus.Connection.CallMethodAsync(_bus.SessionCall(Interface, "Release", Session, options));
    }

    // the EIS socket, which the caller owns and closes
    internal Task<SafeHandle> ConnectToEis() => _bus.ConnectToEis(Interface, Session);

    // closes the session; one whose watch never started was never more than created
    public async ValueTask DisposeAsync()
    {
        if (_watch != null) await _watch.DisposeAsync();
        else await _bus.CloseSession(Session);
    }
}

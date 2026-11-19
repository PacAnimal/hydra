using System.Runtime.InteropServices;
using Microsoft.Extensions.Logging;
using Tmds.DBus.Protocol;

namespace Hydra.Platform.Linux.Wayland;

// An org.freedesktop.portal.RemoteDesktop session for injecting keyboard and pointer input over EIS, the grant kept
// until the user revokes it so a restore token can skip the dialog next time.
internal sealed class RemoteDesktopPortal : IAsyncDisposable
{
    internal const string Interface = "org.freedesktop.portal.RemoteDesktop";
    private const uint DeviceTypes = 1 | 2;   // KEYBOARD | POINTER

    private readonly PortalBus _bus;
    private SessionWatch? _watch;

    private RemoteDesktopPortal(PortalBus bus, ObjectPath session)
    {
        _bus = bus;
        Session = session;
    }

    internal ObjectPath Session { get; }
    internal string? RestoreToken { get; private set; }
    internal bool ClipboardEnabled { get; private set; }
    internal bool ClipboardSupported { get; private set; }

    internal event Action? Closed;

    internal static async Task<RemoteDesktopPortal> Start(PortalBus bus, string? restoreToken, bool wantClipboard, ILogger log, CancellationToken cancel)
    {
        var sessionToken = bus.NextToken();
        var created = await bus.Request(token => bus.Message(PortalBus.DesktopPath, Interface, "CreateSession", "a{sv}", (ref MessageWriter writer) =>
            writer.WriteDictionary(new Dictionary<string, VariantValue>
            {
                ["handle_token"] = token,
                ["session_handle_token"] = sessionToken,
            })), cancel);
        var portal = new RemoteDesktopPortal(bus, PortalBus.SessionHandle(created.Check("Creating the remote desktop session").Results));
        try
        {
            portal._watch = await bus.WatchSession(portal.Session, () => portal.Closed?.Invoke());
            await portal.StartSession(restoreToken, wantClipboard, log, cancel);
            return portal;
        }
        catch
        {
            await portal.DisposeAsync();
            throw;
        }
    }

    private async Task StartSession(string? restoreToken, bool wantClipboard, ILogger log, CancellationToken cancel)
    {
        var selected = await _bus.Request(token =>
        {
            var options = new Dictionary<string, VariantValue>
            {
                ["handle_token"] = token,
                ["types"] = DeviceTypes,
                ["persist_mode"] = PortalBus.PersistUntilRevoked,
            };
            if (restoreToken != null) options["restore_token"] = restoreToken;
            return _bus.SessionCall(Interface, "SelectDevices", Session, options);
        }, cancel);
        selected.Check("Selecting the remote desktop devices");

        ClipboardSupported = await _bus.Has(PortalBus.ClipboardInterface);
        if (wantClipboard) await PortalClipboard.RequestClipboard(_bus, Session);

        var started = await _bus.Request(token => _bus.Message(PortalBus.DesktopPath, Interface, "Start", "osa{sv}", (ref MessageWriter writer) =>
        {
            writer.WriteObjectPath(Session);
            writer.WriteString("");
            writer.WriteDictionary(new Dictionary<string, VariantValue> { ["handle_token"] = token });
        }), cancel);
        started.Check("Starting the remote desktop session");
        RestoreToken = started.String("restore_token");
        ClipboardEnabled = started.Bool("clipboard_enabled");
        PortalBus.LogStarted(log, "Remote desktop", Session, ClipboardEnabled, RestoreToken);
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

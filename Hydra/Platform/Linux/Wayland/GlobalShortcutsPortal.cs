using Microsoft.Extensions.Logging;
using Tmds.DBus.Protocol;

namespace Hydra.Platform.Linux.Wayland;

// a shortcut as the application asks for it: Trigger null leaves the choice to the user
internal sealed record ShortcutRequest(string Id, string Description, string? Trigger);

// a shortcut as the portal bound it, its trigger described for people rather than parsed
internal sealed record BoundShortcut(string Id, string? Description, string? TriggerDescription);

// The warning of shortcuts the portal reports no trigger for, with the lines binding them in Hyprland's config.
// xdg-desktop-portal-hyprland ignores preferred_trigger and reports no trigger even for one the user bound in the
// desktop's own config, so one with none may fire or not. Its owner outlives the portal sessions it binds, so a session
// rebound after the portal closed one warns no more; a new owner warns again
internal sealed class UntriggeredWarning
{
    private int _warned;

    internal void Warn(IReadOnlyList<ShortcutRequest> asked, IReadOnlyList<BoundShortcut> untriggered, ILogger log)
    {
        if (untriggered.Count == 0 || !log.IsEnabled(LogLevel.Warning) || Interlocked.Exchange(ref _warned, 1) != 0) return;
        string Binds(HyprlandConfig config) => string.Join("; ", untriggered.Select(s =>
            WaylandShortcuts.HyprlandBind(config, WaylandSession.AppId, s.Id, asked.FirstOrDefault(r => r.Id == s.Id)?.Trigger)));
        log.LogWarning("{Count} shortcuts have no trigger the portal knows of; if they are not bound in the desktop's " +
            "config, bind them: on Hyprland 0.56 and later, in hyprland.lua: {LuaBinds}; on older Hyprland, in hyprland.conf: " +
            "{ConfBinds}", untriggered.Count, Binds(HyprlandConfig.Lua), Binds(HyprlandConfig.Conf));
    }
}

// An org.freedesktop.portal.GlobalShortcuts session: shortcuts that fire whatever has focus. The interface has no
// restore token or persist mode; the portal keeps what a user granted per app id (PortalBus registers ours), so a later
// session's BindShortcuts is answered from that without a dialog. A session binds once.
internal sealed class GlobalShortcutsPortal : IAsyncDisposable
{
    private const string Interface = "org.freedesktop.portal.GlobalShortcuts";

    private readonly PortalBus _bus;
    private readonly ILogger _log;
    private SessionWatch? _watch;

    private GlobalShortcutsPortal(PortalBus bus, ObjectPath session, ILogger log)
    {
        _bus = bus;
        _log = log;
        Session = session;
    }

    internal ObjectPath Session { get; }

    // shortcut id and the activation's timestamp (milliseconds, undefined base)
    internal event Action<string, ulong>? Activated;
    internal event Action<string, ulong>? Deactivated;
    internal event Action<IReadOnlyList<BoundShortcut>>? ShortcutsChanged;
    // the portal closed the session, or the bus went: its shortcuts no longer fire
    internal event Action? Closed;

    // null when the portal has no GlobalShortcuts interface
    internal static async Task<GlobalShortcutsPortal?> Start(PortalBus bus, ILogger log, CancellationToken cancel)
    {
        if (!await bus.Has(Interface)) return null;
        if (log.IsEnabled(LogLevel.Debug)) log.LogDebug("GlobalShortcuts portal version {Version}", await bus.Version(Interface));
        var sessionToken = bus.NextToken();
        var created = await bus.Request(token => bus.Message(PortalBus.DesktopPath, Interface, "CreateSession", "a{sv}", (ref MessageWriter writer) =>
            writer.WriteDictionary(new Dictionary<string, VariantValue>
            {
                ["handle_token"] = token,
                ["session_handle_token"] = sessionToken,
            })), cancel);
        var portal = new GlobalShortcutsPortal(bus, PortalBus.SessionHandle(created.Check("Creating the global shortcuts session").Results), log);
        try
        {
            await portal.Watch();
            return portal;
        }
        catch
        {
            await portal.DisposeAsync();
            throw;
        }
    }

    private async Task Watch()
    {
        var ours = Session.ToString();
        var watch = _watch = await _bus.WatchSession(Session, () => Closed?.Invoke());
        watch.Add(await WatchShortcutSignal("Activated", ours, (id, time) => Activated?.Invoke(id, time)));
        watch.Add(await WatchShortcutSignal("Deactivated", ours, (id, time) => Deactivated?.Invoke(id, time)));
        var changed = new MatchRule { Type = MessageType.Signal, Interface = Interface, Member = "ShortcutsChanged", Path = PortalBus.DesktopPath };
        watch.Add(await _bus.Watch(changed, (message, _) =>
            {
                var reader = message.GetBodyReader();
                return new ChangedSignal(reader.ReadObjectPathAsString(), ReadShortcuts(ref reader));
            },
            signal =>
            {
                if (signal.Session == ours) ShortcutsChanged?.Invoke(signal.Shortcuts);
            }));
    }

    // Activated and Deactivated: (o session_handle, s shortcut_id, t timestamp, a{sv} options)
    private Task<IDisposable> WatchShortcutSignal(string member, string ours, Action<string, ulong> handler)
    {
        var rule = new MatchRule { Type = MessageType.Signal, Interface = Interface, Member = member, Path = PortalBus.DesktopPath };
        return _bus.Watch(rule, (message, _) =>
            {
                var reader = message.GetBodyReader();
                return new ShortcutSignal(reader.ReadObjectPathAsString(), reader.ReadString(), reader.ReadUInt64());
            },
            signal =>
            {
                if (signal.Session == ours) handler(signal.Id, signal.Timestamp);
            });
    }

    // the shortcuts the portal bound with a trigger it knows, a subset of those asked for; may show the desktop's dialog
    // the first time. Those it reports no trigger for go to untriggered, which warns of them (see UntriggeredWarning)
    internal async Task<IReadOnlyList<BoundShortcut>> BindShortcuts(IReadOnlyList<ShortcutRequest> shortcuts, UntriggeredWarning untriggered,
        CancellationToken cancel)
    {
        var response = await _bus.Request(token => _bus.Message(PortalBus.DesktopPath, Interface, "BindShortcuts", "oa(sa{sv})sa{sv}", (ref MessageWriter writer) =>
        {
            writer.WriteObjectPath(Session);
            var array = writer.WriteArrayStart(DBusType.Struct);
            foreach (var shortcut in shortcuts)
            {
                writer.WriteStructureStart();
                writer.WriteString(shortcut.Id);
                var options = new Dictionary<string, VariantValue> { ["description"] = shortcut.Description };
                if (shortcut.Trigger != null) options["preferred_trigger"] = shortcut.Trigger;
                writer.WriteDictionary(options);
            }
            writer.WriteArrayEnd(array);
            writer.WriteString("");
            writer.WriteDictionary(new Dictionary<string, VariantValue> { ["handle_token"] = token });
        }), cancel);
        response.Check("Binding the global shortcuts");
        var bound = response.Results.TryGetValue("shortcuts", out var result) ? FromVariant(result) : [];
        untriggered.Warn(shortcuts, Unassigned(bound), _log);
        return Assigned(bound);
    }

    // a shortcut the portal bound with no trigger_description, or an empty one, has no keys that fire it
    internal static bool HasTrigger(BoundShortcut shortcut) => !string.IsNullOrEmpty(shortcut.TriggerDescription);

    internal static List<BoundShortcut> Assigned(IEnumerable<BoundShortcut> bound) => [.. bound.Where(HasTrigger)];

    internal static List<BoundShortcut> Unassigned(IEnumerable<BoundShortcut> bound) => [.. bound.Where(s => !HasTrigger(s))];

    // BindShortcuts' a(sa{sv}) shortcuts result
    internal static List<BoundShortcut> FromVariant(VariantValue array)
    {
        var shortcuts = new List<BoundShortcut>(array.Count);
        for (var i = 0; i < array.Count; i++)
        {
            var item = array.GetItem(i);
            var options = item.GetItem(1).GetDictionary<string, VariantValue>();
            shortcuts.Add(new BoundShortcut(item.GetItem(0).GetString(), Text(options, "description"), Text(options, "trigger_description")));
        }
        return shortcuts;
    }

    private static List<BoundShortcut> ReadShortcuts(ref Reader reader)
    {
        var shortcuts = new List<BoundShortcut>();
        var end = reader.ReadArrayStart(DBusType.Struct);
        while (reader.HasNext(end))
        {
            reader.AlignStruct();
            var id = reader.ReadString();
            var options = reader.ReadDictionaryOfStringToVariantValue();
            shortcuts.Add(new BoundShortcut(id, Text(options, "description"), Text(options, "trigger_description")));
        }
        return shortcuts;
    }

    private static string? Text(Dictionary<string, VariantValue> options, string key) =>
        options.TryGetValue(key, out var value) && value.Type == VariantValueType.String ? value.GetString() : null;

    // closes the session; one whose watch never started was never more than created
    public async ValueTask DisposeAsync()
    {
        if (_watch != null) await _watch.DisposeAsync();
        else await _bus.CloseSession(Session);
    }

    private sealed record ShortcutSignal(string Session, string Id, ulong Timestamp);

    private sealed record ChangedSignal(string Session, IReadOnlyList<BoundShortcut> Shortcuts);
}

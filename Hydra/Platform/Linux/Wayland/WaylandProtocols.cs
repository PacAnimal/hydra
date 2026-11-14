using System.Runtime.InteropServices;
using static Hydra.Platform.Linux.Wayland.WaylandClientNativeMethods;

namespace Hydra.Platform.Linux.Wayland;

// one request or event as the protocol XML declares it: its signature in libwayland's syntax ("2" prefix for a
// since-v2 message, "?" before a nullable arg) and, per arg, the interface an object or new_id arg names
internal sealed record WaylandMessageSpec(string Name, string Signature, IReadOnlyList<string?> Types);

internal sealed record WaylandInterfaceSpec(
    string Name,
    int Version,
    IReadOnlyList<WaylandMessageSpec> Requests,
    IReadOnlyList<WaylandMessageSpec> Events);

// the wl_interface tables wayland-scanner would generate for the wlroots virtual input protocols and xdg-output,
// laid out by hand in unmanaged memory. Shared is built once and never freed: libwayland keeps pointers into it
// for every proxy of these interfaces.
internal sealed unsafe class WaylandProtocols
{
    internal const string VirtualPointerManager = "zwlr_virtual_pointer_manager_v1";
    internal const string VirtualPointer = "zwlr_virtual_pointer_v1";
    internal const string VirtualKeyboardManager = "zwp_virtual_keyboard_manager_v1";
    internal const string VirtualKeyboard = "zwp_virtual_keyboard_v1";
    internal const string XdgOutputManager = "zxdg_output_manager_v1";
    internal const string XdgOutput = "zxdg_output_v1";
    internal const string Seat = "wl_seat";
    internal const string Output = "wl_output";

    // -- request opcodes, in XML order --

    internal const uint PointerMotion = 0;
    internal const uint PointerMotionAbsolute = 1;
    internal const uint PointerButton = 2;
    internal const uint PointerAxis = 3;
    internal const uint PointerFrame = 4;
    internal const uint PointerAxisSource = 5;
    internal const uint PointerAxisStop = 6;
    internal const uint PointerAxisDiscrete = 7;
    internal const uint PointerDestroy = 8;

    internal const uint PointerManagerCreate = 0;
    internal const uint PointerManagerDestroy = 1;
    internal const uint PointerManagerCreateWithOutput = 2;

    internal const uint KeyboardKeymap = 0;
    internal const uint KeyboardKey = 1;
    internal const uint KeyboardModifiers = 2;
    internal const uint KeyboardDestroy = 3;

    internal const uint KeyboardManagerCreate = 0;

    internal const uint XdgOutputManagerDestroy = 0;
    internal const uint XdgOutputManagerGet = 1;

    internal const uint XdgOutputDestroy = 0;

    // -- xdg_output event opcodes --

    internal const uint XdgOutputLogicalPosition = 0;
    internal const uint XdgOutputLogicalSize = 1;
    internal const uint XdgOutputDone = 2;
    internal const uint XdgOutputName = 3;
    internal const uint XdgOutputDescription = 4;

    // built once a process, on first use: CountWaylandOutputs needs it before DI
    private static readonly Lazy<WaylandProtocols> SharedTables = new(() =>
    {
        var lib = NativeLibrary.Load(Libwayland);
        return Build(name => NativeLibrary.GetExport(lib, name switch
        {
            Seat => wl_seat_interface,
            Output => wl_output_interface,
            _ => throw new KeyNotFoundException($"no core wl_interface {name}"),
        }));
    });

    internal static IReadOnlyList<WaylandInterfaceSpec> Specs { get; } =
    [
        new(VirtualPointer, 2,
        [
            new("motion", "uff", [null, null, null]),
            new("motion_absolute", "uuuuu", [null, null, null, null, null]),
            new("button", "uuu", [null, null, null]),
            new("axis", "uuf", [null, null, null]),
            new("frame", "", []),
            new("axis_source", "u", [null]),
            new("axis_stop", "uu", [null, null]),
            new("axis_discrete", "uufi", [null, null, null, null]),
            new("destroy", "", []),
        ], []),
        new(VirtualPointerManager, 2,
        [
            new("create_virtual_pointer", "?on", [Seat, VirtualPointer]),
            new("destroy", "", []),
            new("create_virtual_pointer_with_output", "2?o?on", [Seat, Output, VirtualPointer]),
        ], []),
        new(VirtualKeyboard, 1,
        [
            new("keymap", "uhu", [null, null, null]),
            new("key", "uuu", [null, null, null]),
            new("modifiers", "uuuu", [null, null, null, null]),
            new("destroy", "", []),
        ], []),
        new(VirtualKeyboardManager, 1,
        [
            new("create_virtual_keyboard", "on", [Seat, VirtualKeyboard]),
        ], []),
        new(XdgOutputManager, 3,
        [
            new("destroy", "", []),
            new("get_xdg_output", "no", [XdgOutput, Output]),
        ], []),
        new(XdgOutput, 3,
        [
            new("destroy", "", []),
        ],
        [
            new("logical_position", "ii", [null, null]),
            new("logical_size", "ii", [null, null]),
            new("done", "", []),
            new("name", "2s", [null]),
            new("description", "2s", [null]),
        ]),
    ];

    private readonly Dictionary<string, nint> _interfaces;

    private WaylandProtocols(Dictionary<string, nint> interfaces)
    {
        _interfaces = interfaces;
    }

    // the tables over libwayland's own wl_seat and wl_output
    internal static WaylandProtocols Shared => SharedTables.Value;

    // a wl_interface* by name: one of ours, or a core one resolved through external
    internal nint this[string name] => _interfaces.TryGetValue(name, out var found)
        ? found
        : throw new KeyNotFoundException($"no wl_interface table for {name}");

    // builds the tables, resolving interfaces not in Specs (wl_seat, wl_output) through external. Never freed.
    internal static WaylandProtocols Build(Func<string, nint> external)
    {
        var interfaces = new Dictionary<string, nint>();
        // every interface first, so a message can point at one declared after it
        foreach (var spec in Specs)
            interfaces[spec.Name] = (nint)NativeMemory.AllocZeroed((nuint)sizeof(WlInterface));

        nint Resolve(string name)
        {
            if (interfaces.TryGetValue(name, out var found)) return found;
            var resolved = external(name);
            if (resolved == 0) throw new InvalidOperationException($"wl_interface {name} not found");
            interfaces[name] = resolved;
            return resolved;
        }

        foreach (var spec in Specs)
        {
            var table = (WlInterface*)interfaces[spec.Name];
            table->name = Marshal.StringToCoTaskMemUTF8(spec.Name);
            table->version = spec.Version;
            table->method_count = spec.Requests.Count;
            table->methods = Messages(spec.Requests, Resolve);
            table->event_count = spec.Events.Count;
            table->events = Messages(spec.Events, Resolve);
        }
        return new WaylandProtocols(interfaces);
    }

    private static nint Messages(IReadOnlyList<WaylandMessageSpec> messages, Func<string, nint> resolve)
    {
        if (messages.Count == 0) return 0;
        var array = (WlMessage*)NativeMemory.AllocZeroed((nuint)messages.Count, (nuint)sizeof(WlMessage));
        for (var i = 0; i < messages.Count; i++)
        {
            var message = messages[i];
            array[i].name = Marshal.StringToCoTaskMemUTF8(message.Name);
            array[i].signature = Marshal.StringToCoTaskMemUTF8(message.Signature);
            if (message.Types.Count == 0) continue;
            var types = (nint*)NativeMemory.AllocZeroed((nuint)message.Types.Count, (nuint)sizeof(nint));
            for (var t = 0; t < message.Types.Count; t++)
                types[t] = message.Types[t] is { } name ? resolve(name) : 0;
            array[i].types = (nint)types;
        }
        return (nint)array;
    }
}

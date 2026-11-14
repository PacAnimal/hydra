// mirrors <wayland-client-core.h>, and <sys/mman.h> for the keymap fds it hands over
// ReSharper disable InconsistentNaming
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace Hydra.Platform.Linux.Wayland;

internal static unsafe partial class WaylandClientNativeMethods
{
    internal const string Libwayland = "libwayland-client.so.0";
    private const string Libc = "libc";

    internal const uint WL_MARSHAL_FLAG_DESTROY = 1 << 0;

    internal const int PROT_READ = 0x1;
    internal const int MAP_PRIVATE = 0x02;
    internal const nint MAP_FAILED = -1;

    // -- exported wl_interface data symbols, resolved with NativeLibrary.GetExport --

    internal const string wl_registry_interface = "wl_registry_interface";
    internal const string wl_seat_interface = "wl_seat_interface";
    internal const string wl_output_interface = "wl_output_interface";
    internal const string wl_keyboard_interface = "wl_keyboard_interface";

    // -- display --

    [LibraryImport(Libwayland)]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    internal static partial nint wl_display_connect(byte* name);

    [LibraryImport(Libwayland)]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    internal static partial void wl_display_disconnect(nint display);

    [LibraryImport(Libwayland)]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    internal static partial int wl_display_get_fd(nint display);

    [LibraryImport(Libwayland)]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    internal static partial int wl_display_get_error(nint display);

    [LibraryImport(Libwayland)]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    internal static partial int wl_display_roundtrip(nint display);

    [LibraryImport(Libwayland)]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    internal static partial int wl_display_dispatch_pending(nint display);

    [LibraryImport(Libwayland)]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    internal static partial int wl_display_flush(nint display);

    [LibraryImport(Libwayland)]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    internal static partial int wl_display_prepare_read(nint display);

    [LibraryImport(Libwayland)]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    internal static partial int wl_display_read_events(nint display);

    [LibraryImport(Libwayland)]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    internal static partial void wl_display_cancel_read(nint display);

    // -- proxy --

    [LibraryImport(Libwayland)]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    internal static partial nint wl_proxy_marshal_array_flags(nint proxy, uint opcode, nint @interface, uint version, uint flags, WlArgument* args);

    [LibraryImport(Libwayland)]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    internal static partial int wl_proxy_add_dispatcher(
        nint proxy,
        delegate* unmanaged[Cdecl]<nint, nint, uint, WlMessage*, WlArgument*, int> dispatcher_func,
        nint dispatcher_data,
        nint data);

    [LibraryImport(Libwayland)]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    internal static partial void wl_proxy_destroy(nint proxy);

    [LibraryImport(Libwayland)]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    internal static partial uint wl_proxy_get_version(nint proxy);

    // -- libc memory mapping --

    [LibraryImport(Libc)]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    internal static partial nint mmap(nint addr, nuint length, int prot, int flags, int fd, nint offset);

    [LibraryImport(Libc)]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    internal static partial int munmap(nint addr, nuint length);
}

// union wl_argument: 8 bytes on LP64
[StructLayout(LayoutKind.Explicit, Size = 8)]
internal struct WlArgument
{
    [FieldOffset(0)] internal int i;
    [FieldOffset(0)] internal uint u;
    [FieldOffset(0)] internal int f;
    [FieldOffset(0)] internal nint s;
    [FieldOffset(0)] internal nint o;
    [FieldOffset(0)] internal uint n;
    [FieldOffset(0)] internal nint a;
    [FieldOffset(0)] internal int h;
}

// struct wl_message
[StructLayout(LayoutKind.Sequential)]
internal struct WlMessage
{
    internal nint name;
    internal nint signature;
    internal nint types;
}

// struct wl_interface
[StructLayout(LayoutKind.Sequential)]
internal struct WlInterface
{
    internal nint name;
    internal int version;
    internal int method_count;
    internal nint methods;
    internal int event_count;
    internal nint events;
}

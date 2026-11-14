// mirrors <libei-1.0/libei.h>
// ReSharper disable InconsistentNaming
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace Hydra.Platform.Linux.Wayland;

internal static partial class LibeiNativeMethods
{
    internal const string Libei = "libei.so.1";

    // -- enum ei_device_capability --

    internal const int EI_DEVICE_CAP_POINTER = 1 << 0;
    internal const int EI_DEVICE_CAP_POINTER_ABSOLUTE = 1 << 1;
    internal const int EI_DEVICE_CAP_KEYBOARD = 1 << 2;
    internal const int EI_DEVICE_CAP_SCROLL = 1 << 4;
    internal const int EI_DEVICE_CAP_BUTTON = 1 << 5;

    // -- enum ei_keymap_type --

    internal const int EI_KEYMAP_TYPE_XKB = 1;

    // -- enum ei_event_type --

    internal const int EI_EVENT_CONNECT = 1;
    internal const int EI_EVENT_DISCONNECT = 2;
    internal const int EI_EVENT_SEAT_ADDED = 3;
    internal const int EI_EVENT_SEAT_REMOVED = 4;
    internal const int EI_EVENT_DEVICE_ADDED = 5;
    internal const int EI_EVENT_DEVICE_REMOVED = 6;
    internal const int EI_EVENT_DEVICE_PAUSED = 7;
    internal const int EI_EVENT_DEVICE_RESUMED = 8;
    internal const int EI_EVENT_KEYBOARD_MODIFIERS = 9;
    internal const int EI_EVENT_PONG = 90;
    internal const int EI_EVENT_FRAME = 100;
    internal const int EI_EVENT_DEVICE_STOP_EMULATING = 201;
    internal const int EI_EVENT_POINTER_MOTION = 300;
    internal const int EI_EVENT_POINTER_MOTION_ABSOLUTE = 400;
    internal const int EI_EVENT_BUTTON_BUTTON = 500;
    internal const int EI_EVENT_SCROLL_DELTA = 600;
    internal const int EI_EVENT_SCROLL_STOP = 601;
    internal const int EI_EVENT_SCROLL_CANCEL = 602;
    internal const int EI_EVENT_SCROLL_DISCRETE = 603;
    internal const int EI_EVENT_KEYBOARD_KEY = 700;

    // -- context --

    [LibraryImport(Libei)]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    internal static partial nint ei_new_sender(nint user_data);

    [LibraryImport(Libei)]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    internal static partial nint ei_new_receiver(nint user_data);

    [LibraryImport(Libei)]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    internal static partial nint ei_unref(nint ei);

    [LibraryImport(Libei, StringMarshalling = StringMarshalling.Utf8)]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    internal static partial void ei_configure_name(nint ei, string name);

    // takes ownership of fd; returns zero or a negative errno
    [LibraryImport(Libei)]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    internal static partial int ei_setup_backend_fd(nint ei, int fd);

    [LibraryImport(Libei)]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    internal static partial int ei_get_fd(nint ei);

    [LibraryImport(Libei)]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    internal static partial void ei_dispatch(nint ei);

    [LibraryImport(Libei)]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    internal static partial nint ei_get_event(nint ei);

    [LibraryImport(Libei)]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    internal static partial ulong ei_now(nint ei);

    // -- ping (since 1.4) --

    [LibraryImport(Libei)]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    internal static partial nint ei_new_ping(nint ei);

    [LibraryImport(Libei)]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    internal static partial ulong ei_ping_get_id(nint ping);

    [LibraryImport(Libei)]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    internal static partial nint ei_ping_unref(nint ei_ping);

    [LibraryImport(Libei)]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    internal static partial void ei_ping(nint ping);

    // -- seat --

    // ei_seat_bind_capabilities(seat, ...) is variadic: not bound, EiSeatBinder calls it through libffi
    internal const string ei_seat_bind_capabilities = "ei_seat_bind_capabilities";

    [LibraryImport(Libei)]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    internal static partial nint ei_seat_ref(nint seat);

    [LibraryImport(Libei)]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    internal static partial nint ei_seat_unref(nint seat);

    // -- event --

    [LibraryImport(Libei)]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    internal static partial nint ei_event_unref(nint @event);

    [LibraryImport(Libei)]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    internal static partial int ei_event_get_type(nint @event);

    // borrowed: does not add a reference
    [LibraryImport(Libei)]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    internal static partial nint ei_event_get_device(nint @event);

    // borrowed: does not add a reference
    [LibraryImport(Libei)]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    internal static partial nint ei_event_get_seat(nint @event);

    // borrowed; NULL for anything but EI_EVENT_PONG
    [LibraryImport(Libei)]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    internal static partial nint ei_event_pong_get_ping(nint @event);

    [LibraryImport(Libei)]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    internal static partial uint ei_event_keyboard_get_xkb_mods_depressed(nint @event);

    [LibraryImport(Libei)]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    internal static partial uint ei_event_keyboard_get_xkb_mods_latched(nint @event);

    [LibraryImport(Libei)]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    internal static partial uint ei_event_keyboard_get_xkb_mods_locked(nint @event);

    [LibraryImport(Libei)]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    internal static partial uint ei_event_keyboard_get_xkb_group(nint @event);

    [LibraryImport(Libei)]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    internal static partial double ei_event_pointer_get_dx(nint @event);

    [LibraryImport(Libei)]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    internal static partial double ei_event_pointer_get_dy(nint @event);

    [LibraryImport(Libei)]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    internal static partial double ei_event_pointer_get_absolute_x(nint @event);

    [LibraryImport(Libei)]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    internal static partial double ei_event_pointer_get_absolute_y(nint @event);

    [LibraryImport(Libei)]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    internal static partial uint ei_event_button_get_button(nint @event);

    [LibraryImport(Libei)]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    [return: MarshalAs(UnmanagedType.U1)]
    internal static partial bool ei_event_button_get_is_press(nint @event);

    [LibraryImport(Libei)]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    internal static partial double ei_event_scroll_get_dx(nint @event);

    [LibraryImport(Libei)]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    internal static partial double ei_event_scroll_get_dy(nint @event);

    [LibraryImport(Libei)]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    [return: MarshalAs(UnmanagedType.U1)]
    internal static partial bool ei_event_scroll_get_stop_x(nint @event);

    [LibraryImport(Libei)]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    [return: MarshalAs(UnmanagedType.U1)]
    internal static partial bool ei_event_scroll_get_stop_y(nint @event);

    [LibraryImport(Libei)]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    internal static partial int ei_event_scroll_get_discrete_dx(nint @event);

    [LibraryImport(Libei)]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    internal static partial int ei_event_scroll_get_discrete_dy(nint @event);

    [LibraryImport(Libei)]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    internal static partial uint ei_event_keyboard_get_key(nint @event);

    [LibraryImport(Libei)]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    [return: MarshalAs(UnmanagedType.U1)]
    internal static partial bool ei_event_keyboard_get_key_is_press(nint @event);

    // -- device --

    [LibraryImport(Libei)]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    internal static partial nint ei_device_ref(nint device);

    [LibraryImport(Libei)]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    internal static partial nint ei_device_unref(nint device);

    // const char*, owned by the device
    [LibraryImport(Libei)]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    internal static partial nint ei_device_get_name(nint device);

    [LibraryImport(Libei)]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    [return: MarshalAs(UnmanagedType.U1)]
    internal static partial bool ei_device_has_capability(nint device, int cap);

    // borrowed; NULL past the last region
    [LibraryImport(Libei)]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    internal static partial nint ei_device_get_region(nint device, nuint index);

    // borrowed; NULL if the device has no individual keymap
    [LibraryImport(Libei)]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    internal static partial nint ei_device_keyboard_get_keymap(nint device);

    // -- region --

    [LibraryImport(Libei)]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    internal static partial uint ei_region_get_x(nint region);

    [LibraryImport(Libei)]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    internal static partial uint ei_region_get_y(nint region);

    [LibraryImport(Libei)]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    internal static partial uint ei_region_get_width(nint region);

    [LibraryImport(Libei)]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    internal static partial uint ei_region_get_height(nint region);

    // -- keymap --

    [LibraryImport(Libei)]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    internal static partial nuint ei_keymap_get_size(nint keymap);

    [LibraryImport(Libei)]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    internal static partial int ei_keymap_get_type(nint keymap);

    // owned by the keymap: do not close
    [LibraryImport(Libei)]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    internal static partial int ei_keymap_get_fd(nint keymap);

    // -- sender --

    [LibraryImport(Libei)]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    internal static partial void ei_device_start_emulating(nint device, uint sequence);

    [LibraryImport(Libei)]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    internal static partial void ei_device_stop_emulating(nint device);

    [LibraryImport(Libei)]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    internal static partial void ei_device_frame(nint device, ulong time);

    [LibraryImport(Libei)]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    internal static partial void ei_device_pointer_motion(nint device, double x, double y);

    [LibraryImport(Libei)]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    internal static partial void ei_device_pointer_motion_absolute(nint device, double x, double y);

    [LibraryImport(Libei)]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    internal static partial void ei_device_button_button(nint device, uint button, [MarshalAs(UnmanagedType.U1)] bool is_press);

    [LibraryImport(Libei)]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    internal static partial void ei_device_scroll_discrete(nint device, int x, int y);

    [LibraryImport(Libei)]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    internal static partial void ei_device_keyboard_key(nint device, uint keycode, [MarshalAs(UnmanagedType.U1)] bool is_press);
}

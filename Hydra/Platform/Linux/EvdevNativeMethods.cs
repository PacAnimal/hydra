using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
// ReSharper disable InconsistentNaming
// ReSharper disable RedundantCast
// ReSharper disable RedundantOverflowCheckingContext

namespace Hydra.Platform.Linux;

internal static partial class EvdevNativeMethods
{
    private const string Libc = "libc";
    private const string Xkb = "libxkbcommon.so.0";

    // -- open flags --

    internal const int O_RDONLY = 0;
    internal const int O_NONBLOCK = 0x800;

    // -- ioctl commands (_IOC(dir, type, nr, size) = (dir<<30)|(size<<16)|(type<<8)|nr) --

    // EVIOCGRAB: exclusive device grab — _IOW('E', 0x90, int) = 0x40044590
    internal const int EVIOCGRAB = unchecked((int)0x40044590);
    // EVIOCGREP: get repeat settings — _IOR('E', 0x03, uint[2]) = 0x80084503
    internal const int EVIOCGREP = unchecked((int)0x80084503);
    // EVIOCGBIT(0, 1): which event types device supports — 0x80014520
    internal const int EVIOCGBIT_EV = unchecked((int)0x80014520);
    // EVIOCGBIT(EV_KEY=1, 96): key capabilities (96 bytes = 768 bits, covers KEY_MAX=0x2FF) — 0x80604521
    internal const int EVIOCGBIT_EV_KEY = unchecked((int)0x80604521);
    // EVIOCGBIT(EV_REL=2, 2): relative axis capabilities (2 bytes covers REL_HWHEEL=0x06, REL_WHEEL=0x08) — 0x80024522
    internal const int EVIOCGBIT_EV_REL = unchecked((int)0x80024522);

    // -- event types --

    internal const ushort EV_SYN = 0x00;
    internal const ushort EV_KEY = 0x01;
    internal const ushort EV_REL = 0x02;

    // -- relative axis codes --

    internal const ushort REL_X = 0x00;
    internal const ushort REL_Y = 0x01;
    internal const ushort REL_HWHEEL = 0x06;
    internal const ushort REL_WHEEL = 0x08;

    // -- key/button codes --

    internal const ushort BTN_LEFT = 0x110;
    internal const ushort BTN_RIGHT = 0x111;
    internal const ushort BTN_MIDDLE = 0x112;
    internal const ushort BTN_SIDE = 0x113;
    internal const ushort BTN_EXTRA = 0x114;
    internal const ushort KEY_A = 0x1E;       // used to detect keyboard capability

    // -- libxkbcommon direction constants --

    internal const int XKB_KEY_UP = 0;
    internal const int XKB_KEY_DOWN = 1;
    internal const int XKB_STATE_MODS_EFFECTIVE = 1 << 3;
    internal const uint XKB_STATE_LAYOUT_EFFECTIVE = 1 << 7;

    // -- libc file I/O --

    [LibraryImport(Libc, EntryPoint = "open", StringMarshalling = StringMarshalling.Utf8)]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    internal static partial int open(string path, int flags);

    [LibraryImport(Libc, EntryPoint = "close")]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    internal static partial int close(int fd);

    [LibraryImport(Libc, EntryPoint = "read")]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    internal static partial nint read(int fd, ref InputEvent buf, nuint count);

    // -- ioctl overloads --

    [LibraryImport(Libc, EntryPoint = "ioctl")]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    internal static partial int ioctl_grab(int fd, int request, int arg);

    [LibraryImport(Libc, EntryPoint = "ioctl")]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    internal static partial int ioctl_rep(int fd, int request, ref EvdevRepeatSettings arg);

    [LibraryImport(Libc, EntryPoint = "ioctl")]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    internal static partial int ioctl_bit(int fd, int request, [Out] byte[] arg);

    // -- libxkbcommon context / keymap / state --

    [LibraryImport(Xkb)]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    internal static partial nint xkb_context_new(int flags);

    [LibraryImport(Xkb)]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    internal static partial void xkb_context_unref(nint context);

    [LibraryImport(Xkb)]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    internal static partial nint xkb_keymap_new_from_names(nint context, ref XkbRuleNames names, int flags);

    [LibraryImport(Xkb)]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    internal static partial void xkb_keymap_unref(nint keymap);

    [LibraryImport(Xkb)]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    internal static partial nint xkb_state_new(nint keymap);

    [LibraryImport(Xkb)]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    internal static partial void xkb_state_unref(nint state);

    // evdev keycode + 8 = X11 keycode convention used by libxkbcommon
    [LibraryImport(Xkb)]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    internal static partial uint xkb_state_key_get_one_sym(nint state, uint key);

    [LibraryImport(Xkb)]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    internal static partial int xkb_state_update_key(nint state, uint key, int direction);

    [LibraryImport(Xkb, StringMarshalling = StringMarshalling.Utf8)]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    internal static partial int xkb_state_mod_name_is_active(nint state, string name, int type);

    // returns XKB_MOD_INVALID (uint.MaxValue) if the name is not found in the keymap.
    [LibraryImport(Xkb, StringMarshalling = StringMarshalling.Utf8)]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    internal static partial uint xkb_keymap_mod_get_index(nint keymap, string name);

    // returns XKB_KEYCODE_INVALID (uint.MaxValue) if the named key is not in the keymap.
    [LibraryImport(Xkb, StringMarshalling = StringMarshalling.Utf8)]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    internal static partial uint xkb_keymap_key_by_name(nint keymap, string name);

    [LibraryImport(Xkb)]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    internal static partial uint xkb_state_serialize_layout(nint state, uint components);

    // returns the number of keysyms and writes a pointer to the keysym array into syms_out.
    // syms_out points into keymap-owned memory — do not free.
    [LibraryImport(Xkb)]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    internal static partial int xkb_keymap_key_get_syms_by_level(nint keymap, uint key, uint layout, uint level, out nint syms_out);

    // -- helpers --

    internal static bool TestBit(byte[] bits, int n) => n / 8 < bits.Length && (bits[n / 8] & (1 << (n % 8))) != 0;
}

// Linux input_event struct (24 bytes on 64-bit: 8+8 timeval, 2 type, 2 code, 4 value)
[StructLayout(LayoutKind.Sequential)]
internal struct InputEvent
{
    public long TvSec;
    public long TvUsec;
    public ushort Type;
    public ushort Code;
    public int Value;
}

[StructLayout(LayoutKind.Sequential)]
internal struct EvdevRepeatSettings
{
    public uint DelayMs;
    public uint RateMs;
}

// libxkbcommon xkb_rule_names — all fields are const char* (native strings, nint)
[StructLayout(LayoutKind.Sequential)]
internal struct XkbRuleNames
{
    public nint Rules;
    public nint Model;
    public nint Layout;
    public nint Variant;
    public nint Options;
}

using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace Hydra.Platform.Linux;

internal static partial class NativeMethods
{
    private const string X11 = "libX11.so.6";
    private const string Xi = "libXi.so.6";

    // -- event type constants --

    internal const int GenericEvent = 35;

    // -- XI2 event type constants --

    internal const int XI_RawKeyPress = 13;
    internal const int XI_RawKeyRelease = 14;
    internal const int XI_RawMotion = 17;

    // -- XI2 device constants --

    internal const int XIAllMasterDevices = 1;

    // -- grab mode constants --

    internal const int GrabModeAsync = 1;
    internal const int GrabSuccess = 0;

    // -- Xkb constants --

    internal const uint XkbUseCoreKbd = 0x0100;

    // -- window class constant --

    internal const int InputOnly = 2;

    // -- time --

    internal const nuint CurrentTime = 0;

    // -- display --

    [LibraryImport(X11)]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    internal static partial int XInitThreads();

    [LibraryImport(X11, StringMarshalling = StringMarshalling.Utf8)]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    internal static partial nint XOpenDisplay(string? displayName);

    [LibraryImport(X11)]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    internal static partial int XCloseDisplay(nint display);

    [LibraryImport(X11)]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    internal static partial nint XDefaultRootWindow(nint display);

    [LibraryImport(X11)]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    internal static partial int XDefaultScreen(nint display);

    [LibraryImport(X11)]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    internal static partial int XDisplayWidth(nint display, int screen);

    [LibraryImport(X11)]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    internal static partial int XDisplayHeight(nint display, int screen);

    [LibraryImport(X11)]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    internal static partial int XFlush(nint display);

    [LibraryImport(X11)]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    internal static partial int XPending(nint display);

    // -- events --

    [LibraryImport(X11)]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    internal static partial int XNextEvent(nint display, out XEvent @event);

    [LibraryImport(X11)]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool XGetEventData(nint display, ref XEvent cookie);

    [LibraryImport(X11)]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    internal static partial void XFreeEventData(nint display, ref XEvent cookie);

    // -- XI2 --

    [LibraryImport(Xi)]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    internal static partial int XISelectEvents(nint display, nint window, ref XIEventMask mask, int numMasks);

    [LibraryImport(X11, StringMarshalling = StringMarshalling.Utf8)]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool XQueryExtension(nint display, string name, out int majorOpcode, out int eventReturn, out int errorReturn);

    // -- pointer --

    [LibraryImport(X11)]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool XQueryPointer(
        nint display, nint window,
        out nint rootReturn, out nint childReturn,
        out int rootX, out int rootY,
        out int winX, out int winY,
        out uint maskReturn);

    [LibraryImport(X11)]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    internal static partial int XWarpPointer(
        nint display, nint srcWindow, nint destWindow,
        int srcX, int srcY, uint srcWidth, uint srcHeight,
        int destX, int destY);

    // -- cursor --

    [LibraryImport(X11)]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    internal static unsafe partial nint XCreateBitmapFromData(nint display, nint drawable, byte* data, uint width, uint height);

    [LibraryImport(X11)]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    internal static partial nint XCreatePixmapCursor(nint display, nint source, nint mask, ref XColor foreground, ref XColor background, uint x, uint y);

    [LibraryImport(X11)]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    internal static partial int XDefineCursor(nint display, nint window, nint cursor);

    [LibraryImport(X11)]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    internal static partial int XUndefineCursor(nint display, nint window);

    [LibraryImport(X11)]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    internal static partial int XFreeCursor(nint display, nint cursor);

    [LibraryImport(X11)]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    internal static partial int XFreePixmap(nint display, nint pixmap);

    // -- keyboard grabs --

    [LibraryImport(X11)]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    internal static partial int XGrabKeyboard(
        nint display, nint grabWindow,
        [MarshalAs(UnmanagedType.Bool)] bool ownerEvents,
        int pointerMode, int keyboardMode,
        nuint time);

    [LibraryImport(X11)]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    internal static partial int XUngrabKeyboard(nint display, nuint time);

    // -- Xkb keyboard --

    [LibraryImport(X11)]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    internal static partial ulong XkbKeycodeToKeysym(nint display, uint keycode, int group, int level);

    [LibraryImport(X11)]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    internal static partial int XkbGetState(nint display, uint deviceSpec, out XkbStateRec stateReturn);
}

// XEvent union — 192 bytes on 64-bit LP64 (24 longs).
// explicit layout covers only the fields we access; all others are padding.
[StructLayout(LayoutKind.Explicit, Size = 192)]
internal struct XEvent
{
    [FieldOffset(0)] internal int Type;

    // XGenericEventCookie fields (for XI2 raw events)
    [FieldOffset(32)] internal int XCookieExtension;
    [FieldOffset(36)] internal int XCookieEvType;
    [FieldOffset(48)] internal nint XCookieData;   // void* filled by XGetEventData
}

// passed to XISelectEvents — deviceid, mask_len, and a pointer to the event mask bytes
[StructLayout(LayoutKind.Sequential)]
internal struct XIEventMask
{
    internal int DeviceId;
    internal int MaskLen;
    internal nint Mask;   // byte* to the bitmask array
}

// XColor — used when creating blank cursor pixmap
[StructLayout(LayoutKind.Sequential)]
internal struct XColor
{
    internal ulong Pixel;
    internal ushort Red, Green, Blue;
    internal byte Flags;
    internal byte Pad;
}

// subset of XkbStateRec — only the fields we read
[StructLayout(LayoutKind.Sequential)]
internal struct XkbStateRec
{
    internal byte Group;
    internal byte LockedGroup;
    internal ushort BaseGroup;
    internal ushort LatchedGroup;
    internal byte Mods;   // effective modifier mask (ShiftMask, ControlMask, etc.)
}

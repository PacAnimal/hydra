using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace Hydra.Platform.Linux;

internal static partial class NativeMethods
{
    private const string X11 = "libX11.so.6";
    private const string Xi = "libXi.so.6";
    private const string XFixes = "libXfixes.so.3";

    // -- event type constants --

    internal const int GenericEvent = 35;
    internal const int KeyPress = 2;
    internal const int KeyRelease = 3;

    // -- XI2 event type constants --

    internal const int XI_RawButtonPress = 15;
    internal const int XI_RawButtonRelease = 16;
    internal const int XI_RawMotion = 17;

    // -- XI2 device constants --

    internal const int XIAllMasterDevices = 1;

    // -- window class / attribute constants --

    internal const int InputOnly = 2;
    internal const ulong CWOverrideRedirect = 0x200;

    // -- grab constants --

    internal const int GrabModeAsync = 1;
    internal const int GrabSuccess = 0;

    // -- modifier mask constants (X.h) --

    internal const uint ShiftMask = 0x01;
    internal const uint LockMask = 0x02;
    internal const uint ControlMask = 0x04;
    internal const uint Mod1Mask = 0x08;   // Alt
    internal const uint Mod2Mask = 0x10;   // NumLock
    internal const uint Mod4Mask = 0x40;   // Super/Win
    internal const uint Mod5Mask = 0x80;   // AltGr (ISO_Level3_Shift)

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

    // returns the file descriptor of the X11 connection — used with poll() for blocking event wait
    [LibraryImport(X11)]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    internal static partial int XConnectionNumber(nint display);

    // poll() — block until fd is readable or timeout_ms elapses (timeout -1 = block forever)
    internal const short POLLIN = 0x0001;

    [LibraryImport("libc", EntryPoint = "poll")]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    internal static partial int poll(ref PollFd fds, uint nfds, int timeout_ms);

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

    // -- window --

    [LibraryImport(X11)]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    internal static partial nint XCreateWindow(
        nint display, nint parent,
        int x, int y, uint width, uint height,
        uint borderWidth, int depth, uint @class,
        nint visual, ulong valueMask,
        ref XSetWindowAttributes attributes);

    [LibraryImport(X11)]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    internal static partial int XDestroyWindow(nint display, nint window);

    [LibraryImport(X11)]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    internal static partial int XMapWindow(nint display, nint window);

    [LibraryImport(X11)]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    internal static partial int XUnmapWindow(nint display, nint window);

    [LibraryImport(X11)]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    internal static partial int XRaiseWindow(nint display, nint window);

    // -- keyboard grab --

    [LibraryImport(X11)]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    internal static partial int XGrabKeyboard(nint display, nint grabWindow,
        [MarshalAs(UnmanagedType.Bool)] bool ownerEvents,
        int pointerMode, int keyboardMode, nuint time);

    [LibraryImport(X11)]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    internal static partial int XUngrabKeyboard(nint display, nuint time);

    [LibraryImport(X11)]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    internal static partial int XGrabKey(nint display, int keycode, uint modifiers, nint grabWindow,
        [MarshalAs(UnmanagedType.Bool)] bool ownerEvents,
        int pointerMode, int keyboardMode);

    [LibraryImport(X11)]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    internal static partial int XUngrabKey(nint display, int keycode, uint modifiers, nint grabWindow);

    // -- cursor (XFixes) --

    [LibraryImport(XFixes)]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    internal static partial void XFixesHideCursor(nint display, nint window);

    [LibraryImport(XFixes)]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    internal static partial void XFixesShowCursor(nint display, nint window);

    // -- XTest input injection --

    private const string XTest = "libXtst.so.6";

    [LibraryImport(XTest)]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    internal static partial int XTestFakeKeyEvent(nint display, uint keycode, [MarshalAs(UnmanagedType.Bool)] bool isPress, nuint delay);

    [LibraryImport(XTest)]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    internal static partial int XTestFakeButtonEvent(nint display, uint button, [MarshalAs(UnmanagedType.Bool)] bool isPress, nuint delay);

    [LibraryImport(XTest)]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    internal static partial int XTestFakeMotionEvent(nint display, int screenNumber, int x, int y, nuint delay);

    // -- Xkb keyboard --

    [LibraryImport(X11)]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    internal static partial ulong XkbKeycodeToKeysym(nint display, uint keycode, int group, int level);

    [LibraryImport(X11)]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    internal static partial uint XKeysymToKeycode(nint display, ulong keysym);

    // XkbUseCoreKbd = 0x0100 (use the core keyboard device)
    internal const uint XkbUseCoreKbd = 0x0100;

    [LibraryImport(X11)]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool XkbGetAutoRepeatRate(nint display, uint deviceSpec, out uint delay, out uint interval);

    // -- XRandR multi-monitor enumeration --

    private const string XRandR = "libXrandr.so.2";

    // returns nint (XRRScreenResources*) or nint.Zero
    [LibraryImport(XRandR)]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    internal static partial nint XRRGetScreenResources(nint display, nint window);

    // returns nint (XRROutputInfo*) or nint.Zero
    [LibraryImport(XRandR)]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    internal static partial nint XRRGetOutputInfo(nint display, nint resources, nint output);

    // returns nint (XRRCrtcInfo*) or nint.Zero
    [LibraryImport(XRandR)]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    internal static partial nint XRRGetCrtcInfo(nint display, nint resources, nint crtc);

    [LibraryImport(XRandR)]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    internal static partial void XRRFreeScreenResources(nint resources);

    [LibraryImport(XRandR)]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    internal static partial void XRRFreeOutputInfo(nint outputInfo);

    [LibraryImport(XRandR)]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    internal static partial void XRRFreeCrtcInfo(nint crtcInfo);
}

// struct pollfd for use with poll()
[StructLayout(LayoutKind.Sequential)]
internal struct PollFd
{
    internal int Fd;
    internal short Events;
    internal short Revents;
}

// XEvent union — 192 bytes on 64-bit LP64 (24 longs).
// explicit layout covers only the fields we access; all others are padding.
[StructLayout(LayoutKind.Explicit, Size = 192)]
internal struct XEvent
{
    [FieldOffset(0)] internal int Type;

    // XKeyEvent fields (for standard KeyPress/KeyRelease events)
    [FieldOffset(80)] internal uint XKeyState;     // modifier mask
    [FieldOffset(84)] internal uint XKeyKeycode;   // hardware keycode

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

// XSetWindowAttributes — 112 bytes on 64-bit LP64; we only set override_redirect at offset 88
[StructLayout(LayoutKind.Explicit, Size = 112)]
internal struct XSetWindowAttributes
{
    [FieldOffset(88)] internal int OverrideRedirect;
}

// minimal XIRawEvent layout — covers only the detail field (button number / key detail).
// full struct: int type, ulong serial, int send_event, Display* display, int extension, int evtype,
// ulong time, int deviceid, int sourceid, int detail → offset 56 on 64-bit LP64.
[StructLayout(LayoutKind.Explicit)]
internal struct XIRawEvent
{
    [FieldOffset(56)] internal int Detail;
}

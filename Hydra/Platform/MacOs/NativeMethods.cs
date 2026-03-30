using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace Hydra.Platform.MacOs;

internal static partial class NativeMethods
{
    private const string CoreGraphics = "/System/Library/Frameworks/CoreGraphics.framework/CoreGraphics";
    private const string CoreFoundation = "/System/Library/Frameworks/CoreFoundation.framework/CoreFoundation";
    private const string ApplicationServices = "/System/Library/Frameworks/ApplicationServices.framework/ApplicationServices";

    // -- event tap constants --

    internal const int KCGHidEventTap = 0;
    internal const int KCGHeadInsertEventTap = 0;
    internal const int KCGEventTapOptionDefault = 0;
    internal const ulong KCGEventMaskForAllEvents = ~0UL;
    internal const int KCGEventTapDisabledByTimeout = unchecked((int)0xFFFFFFFE);
    internal const int KCGEventMouseMoved = 5;
    internal const int KCGEventLeftMouseDragged = 6;
    internal const int KCGEventRightMouseDragged = 7;
    internal const int KCGEventOtherMouseDragged = 27;

    // -- CoreGraphics private APIs (CGS) --

    [LibraryImport(CoreGraphics)]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    internal static partial int CGSMainConnectionID();

    [LibraryImport(CoreGraphics)]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    internal static partial int CGSSetConnectionProperty(int cid, int targetCid, nint key, nint value);

    // -- CoreFoundation: string + boolean --

    [LibraryImport(CoreFoundation, StringMarshalling = StringMarshalling.Utf8)]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    internal static partial nint CFStringCreateWithCString(nint allocator, string str, uint encoding);

    internal const uint KCFStringEncodingUtf8 = 0x08000100;

    // -- ApplicationServices --

    [LibraryImport(ApplicationServices)]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool AXIsProcessTrusted();

    // -- CoreGraphics: display --

    [LibraryImport(CoreGraphics)]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    internal static partial uint CGMainDisplayID();

    [LibraryImport(CoreGraphics)]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    internal static partial CGRect CGDisplayBounds(uint display);

    [LibraryImport(CoreGraphics)]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    internal static partial int CGDisplayHideCursor(uint display);

    [LibraryImport(CoreGraphics)]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    internal static partial int CGDisplayShowCursor(uint display);

    [LibraryImport(CoreGraphics)]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    internal static partial int CGAssociateMouseAndMouseCursorPosition([MarshalAs(UnmanagedType.Bool)] bool connected);

    // -- CoreGraphics: cursor --

    [LibraryImport(CoreGraphics)]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    internal static partial int CGWarpMouseCursorPosition(CGPoint point);

    // setting near-zero prevents CGWarpMouseCursorPosition from resetting the acceleration curve
    [LibraryImport(CoreGraphics)]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    internal static partial void CGSetLocalEventsSuppressionInterval(double seconds);

    // -- CoreGraphics: events --

    internal const int KCGMouseEventDeltaX = 4;
    internal const int KCGMouseEventDeltaY = 5;

    [LibraryImport(CoreGraphics)]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    internal static partial long CGEventGetIntegerValueField(nint eventRef, int field);

    [LibraryImport(CoreGraphics)]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    internal static partial nint CGEventTapCreate(
        int tap, int place, int options,
        ulong eventsOfInterest,
        CGEventTapCallBack callback,
        nint userInfo);

    [LibraryImport(CoreGraphics)]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    internal static partial CGPoint CGEventGetLocation(nint eventRef);

    [LibraryImport(CoreGraphics)]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    internal static partial void CGEventTapEnable(nint tap, [MarshalAs(UnmanagedType.Bool)] bool enable);

    // -- CoreFoundation: run loop --

    [LibraryImport(CoreFoundation)]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    internal static partial nint CFMachPortCreateRunLoopSource(nint allocator, nint port, nint order);

    [LibraryImport(CoreFoundation)]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    internal static partial nint CFRunLoopGetCurrent();

    [LibraryImport(CoreFoundation)]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    internal static partial void CFRunLoopAddSource(nint rl, nint source, nint mode);

    [LibraryImport(CoreFoundation)]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    internal static partial void CFRunLoopRun();

    [LibraryImport(CoreFoundation)]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    internal static partial void CFRunLoopStop(nint rl);

    [LibraryImport(CoreFoundation)]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    internal static partial void CFRelease(nint cf);

    // kCFRunLoopCommonModes string constant – queried via CFStringCreateWithCString is complex;
    // instead, read the symbol pointer directly from the framework
    [LibraryImport(CoreFoundation)]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    internal static partial nint CFRunLoopCopyCurrentMode(nint rl);
}

[UnmanagedFunctionPointer(CallingConvention.Cdecl)]
internal delegate nint CGEventTapCallBack(nint proxy, int type, nint eventRef, nint userInfo);

[StructLayout(LayoutKind.Sequential)]
internal struct CGPoint
{
    internal double X;
    internal double Y;
}

[StructLayout(LayoutKind.Sequential)]
internal struct CGRect
{
    internal CGPoint Origin;
    internal CGPoint Size;
}

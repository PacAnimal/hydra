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
    internal const int KCGEventLeftMouseDown = 1;
    internal const int KCGEventLeftMouseUp = 2;
    internal const int KCGEventRightMouseDown = 3;
    internal const int KCGEventRightMouseUp = 4;
    internal const int KCGEventMouseMoved = 5;
    internal const int KCGEventLeftMouseDragged = 6;
    internal const int KCGEventRightMouseDragged = 7;
    internal const int KCGEventOtherMouseDragged = 27;
    internal const int KCGEventScrollWheel = 22;
    internal const int KCGEventOtherMouseDown = 25;
    internal const int KCGEventOtherMouseUp = 26;

    // CGEventField values for mouse movement deltas and button number
    internal const int KCGMouseEventDeltaX = 5;
    internal const int KCGMouseEventDeltaY = 6;
    internal const int KCGMouseEventButtonNumber = 3;
    internal const int KCGScrollWheelEventDeltaAxis1 = 11;        // integer line delta, vertical (positive = up)
    internal const int KCGScrollWheelEventDeltaAxis2 = 12;        // integer line delta, horizontal (positive = right)
    internal const int KCGScrollWheelEventFixedPtDeltaAxis1 = 93; // 16.16 fixed-point line delta, vertical
    internal const int KCGScrollWheelEventFixedPtDeltaAxis2 = 94; // 16.16 fixed-point line delta, horizontal
    internal const int KCGScrollWheelEventPointDeltaAxis1 = 96;   // pixel delta, vertical
    internal const int KCGScrollWheelEventPointDeltaAxis2 = 97;   // pixel delta, horizontal

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
    internal static unsafe partial int CGGetActiveDisplayList(uint maxDisplays, uint* activeDisplays, out uint displayCount);

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

    // -- CoreGraphics: keyboard event types and fields --

    internal const int KCGEventKeyDown = 10;
    internal const int KCGEventKeyUp = 11;
    internal const int KCGEventFlagsChanged = 12;

    internal const int KCGKeyboardEventKeycode = 9;

    // CGEventFlags modifier mask bits
    internal const ulong KCGEventFlagMaskAlphaShift = 0x00010000; // caps lock
    internal const ulong KCGEventFlagMaskShift = 0x00020000;
    internal const ulong KCGEventFlagMaskControl = 0x00040000;
    internal const ulong KCGEventFlagMaskAlternate = 0x00080000; // option/alt
    internal const ulong KCGEventFlagMaskCommand = 0x00100000;
    internal const ulong KCGEventFlagMaskNumericPad = 0x00200000;

    [LibraryImport(CoreGraphics)]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    internal static partial ulong CGEventGetFlags(nint eventRef);

    [LibraryImport(CoreGraphics)]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    internal static partial void CGEventSetFlags(nint eventRef, ulong flags);

    [LibraryImport(CoreGraphics)]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    internal static partial void CGEventSetType(nint eventRef, int eventType);

    // kCGEventSourceStateCombinedSessionState = 0: posted events update the session-level modifier
    // tracking, which is what [NSEvent modifierFlags] (class method) reads.
    internal const int KCGEventSourceStateCombinedSessionState = 0;

    [LibraryImport(CoreGraphics)]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    internal static partial nint CGEventSourceCreate(int stateID);

    // -- CoreGraphics: event creation and injection --

    [LibraryImport(CoreGraphics)]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    internal static partial nint CGEventCreateKeyboardEvent(nint source, ushort virtualKey, [MarshalAs(UnmanagedType.Bool)] bool keyDown);

    [LibraryImport(CoreGraphics)]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    internal static partial nint CGEventCreateMouseEvent(nint source, int mouseType, CGPoint mouseCursorPosition, int mouseButton);

    [LibraryImport(CoreGraphics)]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    internal static partial nint CGEventCreateScrollWheelEvent(nint source, int units, uint wheelCount, int wheel1, int wheel2);

    [LibraryImport(CoreGraphics)]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    internal static partial void CGEventPost(int tap, nint eventRef);

    // create a blank event (used to query current cursor position before relative move)
    [LibraryImport(CoreGraphics)]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    internal static partial nint CGEventCreate(nint source);

    [LibraryImport(CoreGraphics)]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    internal static partial void CGEventSetIntegerValueField(nint eventRef, int field, long value);

    // set double delta field — required for some 3D apps that read CGEventGetDoubleValueField (barrier/deskflow comment)
    [LibraryImport(CoreGraphics)]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    internal static partial void CGEventSetDoubleValueField(nint eventRef, int field, double value);

    [LibraryImport(CoreGraphics)]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    internal static unsafe partial void CGEventKeyboardSetUnicodeString(nint eventRef, nuint stringLength, ushort* unicodeString);

    // -- CoreGraphics: events (read) --

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

    // -- CoreFoundation: data --

    [LibraryImport(CoreFoundation)]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    internal static partial nint CFDataGetBytePtr(nint theData);

    // -- Carbon: text input sources --

    private const string Carbon = "/System/Library/Frameworks/Carbon.framework/Carbon";

    [LibraryImport(Carbon)]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    internal static partial nint TISCopyCurrentKeyboardLayoutInputSource();

    [LibraryImport(Carbon)]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    internal static partial nint TISGetInputSourceProperty(nint inputSource, nint propertyKey);

    // -- Carbon: UCKeyTranslate --

    // kUCKeyAction values
    internal const ushort KUCKeyActionDown = 0;

    [LibraryImport(Carbon)]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    internal static unsafe partial int UCKeyTranslate(
        nint keyLayoutPtr,
        ushort virtualKeyCode,
        ushort keyAction,
        uint modifierKeyState,
        uint keyboardType,
        uint keyTranslateOptions,
        ref uint deadKeyState,
        nuint maxStringLength,
        out nuint actualStringLength,
        ushort* unicodeString);

    // -- Carbon: keyboard type --

    [LibraryImport(Carbon)]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    internal static partial byte LMGetKbdType();

    // -- Objective-C runtime (used for NX_SYSDEFINED media key decoding) --

    private const string ObjC = "/usr/lib/libobjc.A.dylib";

    [LibraryImport(ObjC, StringMarshalling = StringMarshalling.Utf8)]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    internal static partial nint objc_getClass(string name);

    [LibraryImport(ObjC, StringMarshalling = StringMarshalling.Utf8)]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    internal static partial nint sel_registerName(string str);

    // receiver + selector → nint (no arguments, returns object)
    [LibraryImport(ObjC, EntryPoint = "objc_msgSend")]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    internal static partial nint objc_msgSend_noarg(nint obj, nint sel);

    // receiver + selector + one nint argument → nint (used for class method calls with one arg)
    [LibraryImport(ObjC)]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    internal static partial nint objc_msgSend(nint obj, nint sel, nint arg);

    // receiver + selector + nuint index → nint (for NSArray objectAtIndex:)
    [LibraryImport(ObjC, EntryPoint = "objc_msgSend")]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    internal static partial nint objc_msgSend_nuint(nint obj, nint sel, nuint arg);

    // receiver + selector → long (used for NSInteger return values)
    [LibraryImport(ObjC, EntryPoint = "objc_msgSend")]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    internal static partial long objc_msgSend_long(nint obj, nint sel);

    // receiver + selector → uint (for NSNumber unsignedIntValue)
    [LibraryImport(ObjC, EntryPoint = "objc_msgSend")]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    internal static partial uint objc_msgSend_uint(nint obj, nint sel);

    // receiver + selector → double (for NSTimeInterval return values like keyRepeatDelay)
    [LibraryImport(ObjC, EntryPoint = "objc_msgSend")]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    internal static partial double objc_msgSend_double(nint obj, nint sel);

    // NX_SYSDEFINED event type constant (NSSystemDefined, subtype 8 = media key)
    internal const int KNXSysDefined = 14;

    // NX_KEYTYPE_* constants (from IOKit/hidsystem/ev_keymap.h)
    internal const uint NXKeytypeSoundUp = 0;
    internal const uint NXKeytypeSoundDown = 1;
    internal const uint NXKeytypeBrightnessUp = 2;
    internal const uint NXKeytypeBrightnessDown = 3;
    internal const uint NXKeytypeMute = 7;
    internal const uint NXKeytypeEject = 14;
    internal const uint NXKeytypePlay = 16;
    internal const uint NXKeytypeNext = 17;
    internal const uint NXKeytypePrevious = 18;
    internal const uint NXKeytypeFast = 19;
    internal const uint NXKeytypeRewind = 20;

    // -- IOKit: HID system event injection (deskflow/barrier approach) --
    // IOHIDPostEvent posts events at the IOKit HID driver level, below CoreGraphics.
    // This updates the system-wide modifier state read by [NSEvent modifierFlags] class method,
    // which CGEventPost alone does not do. Deprecated since macOS 11 but still functional.

    private const string IOKit = "/System/Library/Frameworks/IOKit.framework/IOKit";

    // NX event types (IOLLEvent.h) — same numeric values as the CG equivalents
    internal const uint NxFlagsChanged = 12;
    internal const uint NxKeyDown = 10;
    internal const uint NxKeyUp = 11;

    // kNXEventDataVersion (IOLLEvent.h)
    internal const uint KNxEventDataVersion = 2;

    // kIOHIDParamConnectType (IOHIDShared.h) — connection type for IOServiceOpen
    internal const uint KIoHidParamConnectType = 1;

    // device-dependent modifier masks (IOLLEvent.h) — combined with generic CGEventFlag masks
    internal const uint NxDeviceLCmdKeyMask = 0x00000008;
    internal const uint NxDeviceLShiftKeyMask = 0x00000002;
    internal const uint NxDeviceLCtlKeyMask = 0x00000001;
    internal const uint NxDeviceLAltKeyMask = 0x00000020;

    [LibraryImport(IOKit)]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    internal static partial int IOMasterPort(uint bootstrapPort, out uint masterPort);

    [LibraryImport(IOKit, StringMarshalling = StringMarshalling.Utf8)]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    internal static partial nint IOServiceMatching(string name);

    [LibraryImport(IOKit)]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    internal static partial int IOServiceGetMatchingServices(uint masterPort, nint matching, out uint iterator);

    [LibraryImport(IOKit)]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    internal static partial uint IOIteratorNext(uint iterator);

    [LibraryImport(IOKit)]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    internal static partial int IOServiceOpen(uint service, uint owningTask, uint type, out uint connect);

    [LibraryImport(IOKit)]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    internal static partial int IOObjectRelease(uint obj);

    [LibraryImport(IOKit)]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    internal static partial int IOHIDPostEvent(uint connect, uint eventType, IOGPoint location, in NXEventData eventData, uint eventDataVersion, uint eventFlags, uint options);

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

// IOGPoint: cursor location passed to IOHIDPostEvent — verified sizeof=4 via SDK header
[StructLayout(LayoutKind.Sequential)]
internal struct IOGPoint
{
    internal short X;
    internal short Y;
}

// NXEventData: union from IOLLEvent.h — verified sizeof=48, keyCode at offset 8 via SDK header
[StructLayout(LayoutKind.Explicit, Size = 48)]
internal struct NXEventData
{
    [FieldOffset(8)]
    internal ushort KeyCode;
}


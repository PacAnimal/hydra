using System.Runtime.InteropServices;

namespace Hydra.Platform.MacOs;

internal static class MacDisplayHelper
{
    private const uint MaxDisplays = 32;

    // AppKit's screen objects are not thread-safe, and two of our loops ask for them at once: ScreenDetector
    // and NetworkWatcher's screen-count probe both arrive here from their own thread-pool threads, and both
    // start together. -[NSScreen localizedName] fills a Swift-side cache on first use, and two first calls on
    // one screen release the same object twice — reproduced with nothing but AppKit and eight threads, at 3
    // crashes in 200 fresh processes. Under .NET that fault does not even crash: the runtime keeps resuming
    // the faulting thread, it never reaches a GC safe point, and the whole process wedges at the next GC —
    // which is how a macOS slave sat off the relay at full CPU after a self-update restart, until killed.
    // Everything that touches NSScreen goes through this lock, inside a pool so the autoreleased screens and
    // strings are drained before the next caller gets in.
    private static readonly Lock NsScreenLock = new();

    internal static unsafe List<DetectedScreen> GetAllScreens()
    {
        var result = new List<DetectedScreen>();

        var ids = stackalloc uint[(int)MaxDisplays];
        if (NativeMethods.CGGetActiveDisplayList(MaxDisplays, ids, out var count) != 0)
            return [GetPrimaryFallback()];

        Dictionary<uint, string?> nameMap;
        lock (NsScreenLock)
        {
            using var pool = new ObjcAutoreleasePool();
            nameMap = BuildNameMap();
        }

        for (uint i = 0; i < count; i++)
        {
            var displayId = ids[i];
            var bounds = NativeMethods.CGDisplayBounds(displayId);
            nameMap.TryGetValue(displayId, out var name);
            result.Add(new DetectedScreen(
                X: (int)bounds.Origin.X,
                Y: (int)bounds.Origin.Y,
                Width: (int)bounds.Size.X,
                Height: (int)bounds.Size.Y,
                DisplayName: name,
                OutputName: null,
                PlatformId: displayId.ToString()));
        }

        return result.Count > 0 ? result : [GetPrimaryFallback()];
    }

    // builds display-id → NSScreen.localizedName map via ObjC runtime — only ever under NsScreenLock
    private static Dictionary<uint, string?> BuildNameMap()
    {
        var map = new Dictionary<uint, string?>();
        try
        {
            var nsScreenClass = NativeMethods.objc_getClass("NSScreen");
            if (nsScreenClass == nint.Zero) return map;

            var selScreens = NativeMethods.sel_registerName("screens");
            var selCount = NativeMethods.sel_registerName("count");
            var selAtIndex = NativeMethods.sel_registerName("objectAtIndex:");
            var selDeviceDesc = NativeMethods.sel_registerName("deviceDescription");
            var selObjForKey = NativeMethods.sel_registerName("objectForKey:");
            var selUintVal = NativeMethods.sel_registerName("unsignedIntValue");
            var selLocName = NativeMethods.sel_registerName("localizedName");
            var selUtf8 = NativeMethods.sel_registerName("UTF8String");

            var screens = NativeMethods.objc_msgSend_noarg(nsScreenClass, selScreens);
            if (screens == nint.Zero) return map;

            var count = (nuint)NativeMethods.objc_msgSend_long(screens, selCount);
            var nsScreenNumberKey = NativeMethods.CFStringCreateWithCString(
                nint.Zero, "NSScreenNumber", NativeMethods.KCFStringEncodingUtf8);

            for (nuint i = 0; i < count; i++)
            {
                var screen = NativeMethods.objc_msgSend_nuint(screens, selAtIndex, i);
                if (screen == nint.Zero) continue;

                var desc = NativeMethods.objc_msgSend_noarg(screen, selDeviceDesc);
                if (desc == nint.Zero) continue;

                var numObj = NativeMethods.objc_msgSend(desc, selObjForKey, nsScreenNumberKey);
                if (numObj == nint.Zero) continue;

                var displayId = NativeMethods.objc_msgSend_uint(numObj, selUintVal);

                // localizedName added in macOS 12; null on older versions
                var nameStr = NativeMethods.objc_msgSend_noarg(screen, selLocName);
                string? name = null;
                if (nameStr != nint.Zero)
                {
                    var utf8Ptr = NativeMethods.objc_msgSend_noarg(nameStr, selUtf8);
                    if (utf8Ptr != nint.Zero)
                        name = Marshal.PtrToStringUTF8(utf8Ptr);
                }
                map[displayId] = name;
            }

            NativeMethods.CFRelease(nsScreenNumberKey);
        }
        catch (Exception)
        {
            // NSScreen not available in all contexts (e.g., before NSApplication initialisation)
        }
        return map;
    }

    private static DetectedScreen GetPrimaryFallback()
    {
        var display = NativeMethods.CGMainDisplayID();
        var bounds = NativeMethods.CGDisplayBounds(display);
        return new DetectedScreen(0, 0, (int)bounds.Size.X, (int)bounds.Size.Y, null, null, display.ToString());
    }
}

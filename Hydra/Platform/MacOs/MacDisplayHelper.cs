using System.Runtime.InteropServices;
using Hydra.Screen;

namespace Hydra.Platform.MacOs;

internal static class MacDisplayHelper
{
    private const uint MaxDisplays = 32;

    // NSScreen isn't thread-safe (concurrent first use double-frees and wedges the runtime): everything that
    // touches it goes through this lock, inside a pool so its autoreleased objects drain before the next caller
    private static readonly Lock NsScreenLock = new();

    internal static ScreenPoint? CursorPosition()
    {
        var eventRef = NativeMethods.CGEventCreate(nint.Zero);
        if (eventRef == nint.Zero) return null;
        var pos = NativeMethods.CGEventGetLocation(eventRef);
        NativeMethods.CFRelease(eventRef);
        return new ScreenPoint((int)pos.X, (int)pos.Y);
    }

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
            using var nsScreenNumberKey = new NsString("NSScreenNumber");

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

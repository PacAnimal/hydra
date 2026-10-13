using System.Runtime.InteropServices;
using Hydra.Screen;

namespace Hydra.Platform.Linux;

internal static class XorgDisplayHelper
{
    internal static ScreenPoint? CursorPosition(nint display, nint rootWindow)
    {
        if (display == nint.Zero) return null;
        NativeMethods.XQueryPointer(display, rootWindow, out _, out _, out var rootX, out var rootY, out _, out _, out _);
        return new ScreenPoint(rootX, rootY);
    }

    // the keycodes held down on the server right now
    internal static List<uint> HeldKeycodes(nint display)
    {
        var keymap = new byte[32];
        _ = NativeMethods.XQueryKeymap(display, keymap);
        var held = new List<uint>();
        for (var keycode = 0; keycode < keymap.Length * 8; keycode++)
            if ((keymap[keycode >> 3] & (1 << (keycode & 7))) != 0) held.Add((uint)keycode);
        return held;
    }

    // auto-repeat then arrives as repeated KeyPress with no KeyRelease between, on this connection only.
    // false if the server does not support it.
    internal static bool EnableDetectableAutoRepeat(nint display) =>
        NativeMethods.XkbSetDetectableAutoRepeat(display, true, out var supported) && supported;

    internal static List<DetectedScreen> GetAllScreens(nint display, nint rootWindow)
    {
        var result = new List<DetectedScreen>();

        var resources = NativeMethods.XRRGetScreenResources(display, rootWindow);
        if (resources == nint.Zero)
            return GetPrimaryFallback(display);

        try
        {
            // XRRScreenResources: int timestamp, int configTimestamp, int ncrtc, RRCrtc* crtcs,
            //   int noutput at offset 32, RROutput* outputs at offset 40 (64-bit LP64)
            var nOutput = Marshal.ReadInt32(resources, 32);
            var outputsPtr = Marshal.ReadIntPtr(resources, 40);

            for (var i = 0; i < nOutput; i++)
            {
                var output = Marshal.ReadIntPtr(outputsPtr, i * nint.Size);
                var outputInfo = NativeMethods.XRRGetOutputInfo(display, resources, output);
                if (outputInfo == nint.Zero) continue;

                try
                {
                    // XRROutputInfo (64-bit LP64):
                    //   offset  8: RRCrtc crtc
                    //   offset 16: char* name
                    //   offset 24: int nameLen
                    //   offset 48: Connection connection (RR_Connected = 0)
                    var connection = Marshal.ReadInt16(outputInfo, 48);
                    if (connection != 0) continue; // not connected

                    var crtc = Marshal.ReadIntPtr(outputInfo, 8);
                    if (crtc == nint.Zero) continue; // no crtc (off/disabled)

                    var namePtr = Marshal.ReadIntPtr(outputInfo, 16);
                    var nameLen = Marshal.ReadInt32(outputInfo, 24);
                    var name = nameLen > 0 ? Marshal.PtrToStringAnsi(namePtr, nameLen) : null;

                    var crtcInfo = NativeMethods.XRRGetCrtcInfo(display, resources, crtc);
                    if (crtcInfo == nint.Zero) continue;

                    try
                    {
                        // XRRCrtcInfo (64-bit LP64):
                        //   offset  8: int x
                        //   offset 12: int y
                        //   offset 16: int width
                        //   offset 20: int height
                        var x = Marshal.ReadInt32(crtcInfo, 8);
                        var y = Marshal.ReadInt32(crtcInfo, 12);
                        var width = Marshal.ReadInt32(crtcInfo, 16);
                        var height = Marshal.ReadInt32(crtcInfo, 20);

                        if (width > 0 && height > 0)
                            result.Add(new DetectedScreen(x, y, width, height, null, name, output.ToString()));
                    }
                    finally
                    {
                        NativeMethods.XRRFreeCrtcInfo(crtcInfo);
                    }
                }
                finally
                {
                    NativeMethods.XRRFreeOutputInfo(outputInfo);
                }
            }
        }
        finally
        {
            NativeMethods.XRRFreeScreenResources(resources);
        }

        return result.Count > 0 ? result : GetPrimaryFallback(display);
    }

    private static List<DetectedScreen> GetPrimaryFallback(nint display)
    {
        var screen = NativeMethods.XDefaultScreen(display);
        var w = NativeMethods.XDisplayWidth(display, screen);
        var h = NativeMethods.XDisplayHeight(display, screen);
        return [new DetectedScreen(0, 0, w, h, null, null, null)];
    }
}

using Hydra.Screen;
using Microsoft.Extensions.Logging;

namespace Hydra.Platform.MacOs;

// macOS's IWindowFocus. CoreGraphics lists the windows front to back; the picked one is raised through Accessibility,
// which Hydra holds for its event tap, and its app brought to the front. Off the caller's thread, one at a time, since a
// call to an app that has stopped answering blocks until the messaging timeout.
public sealed class MacWindowFocus(ILogger<MacWindowFocus> log) : IWindowFocus
{
    // how long an Accessibility call to one app may block
    private const float AppTimeoutSeconds = 1;

    private readonly Lock _lock = new();

    public void FocusFrontmostOn(ScreenBounds screen) => _ = Task.Run(() =>
    {
        lock (_lock)
        {
            if (WindowPicker.FrontmostOn(OrdinaryWindows(), screen) is not { } window)
            {
                if (log.IsEnabled(LogLevel.Debug)) log.LogDebug("Window focus: no window on {Screen}", screen);
            }
            else if (!Raise(window) && log.IsEnabled(LogLevel.Debug))
                log.LogDebug("Window focus: the window of process {Pid} could not be raised", window.Pid);
        }
    });

    // on-screen windows of the normal layer, front to back: menus, the Dock, panels and the shield live on other layers
    private static List<ListedWindow> OrdinaryWindows()
    {
        var listed = new List<ListedWindow>();
        var info = NativeMethods.CGWindowListCopyWindowInfo(
            NativeMethods.KCGWindowListOptionOnScreenOnly | NativeMethods.KCGWindowListExcludeDesktopElements, 0);
        if (info == 0) return listed;
        using NsString layerKey = new("kCGWindowLayer"), pidKey = new("kCGWindowOwnerPID"), numberKey = new("kCGWindowNumber"), boundsKey = new("kCGWindowBounds");
        try
        {
            var self = Environment.ProcessId;
            for (nint i = 0, n = NativeMethods.CFArrayGetCount(info); i < n; i++)
            {
                var window = NativeMethods.CFArrayGetValueAtIndex(info, i);
                if (Number(window, layerKey) != 0 || Number(window, pidKey) is not { } pid || pid == self) continue;
                if (!NativeMethods.CGRectMakeWithDictionaryRepresentation(NativeMethods.CFDictionaryGetValue(window, boundsKey), out var r)) continue;
                var bounds = new ScreenBounds((int)r.Origin.X, (int)r.Origin.Y, (int)r.Size.X, (int)r.Size.Y);
                listed.Add(new ListedWindow(Number(window, numberKey) ?? 0, pid, bounds));
            }
        }
        finally
        {
            NativeMethods.CFRelease(info);
        }
        return listed;
    }

    private static int? Number(nint dict, nint key)
    {
        var number = NativeMethods.CFDictionaryGetValue(dict, key);
        return number != 0 && NativeMethods.CFNumberGetValue(number, NativeMethods.KCFNumberSInt32Type, out var value) ? value : null;
    }

    // the app's window at those bounds raised and made main, and the app brought to the front
    private static bool Raise(ListedWindow target)
    {
        var app = NativeMethods.AXUIElementCreateApplication(target.Pid);
        if (app == 0) return false;
        using NsString windowsKey = new("AXWindows"), positionKey = new("AXPosition"), sizeKey = new("AXSize"),
            raise = new("AXRaise"), mainKey = new("AXMain"), frontmostKey = new("AXFrontmost");
        try
        {
            _ = NativeMethods.AXUIElementSetMessagingTimeout(app, AppTimeoutSeconds);
            if (NativeMethods.AXUIElementCopyAttributeValue(app, windowsKey, out var windows) != NativeMethods.KAXErrorSuccess || windows == 0) return false;
            try
            {
                for (nint i = 0, n = NativeMethods.CFArrayGetCount(windows); i < n; i++)
                {
                    var window = NativeMethods.CFArrayGetValueAtIndex(windows, i);
                    if (!IsAt(window, positionKey, sizeKey, target.Bounds)) continue;
                    _ = NativeMethods.AXUIElementPerformAction(window, raise);
                    _ = NativeMethods.AXUIElementSetAttributeValue(window, mainKey, NativeMethods.KCFBooleanTrue);
                    return NativeMethods.AXUIElementSetAttributeValue(app, frontmostKey, NativeMethods.KCFBooleanTrue) == NativeMethods.KAXErrorSuccess;
                }
                return false;
            }
            finally
            {
                NativeMethods.CFRelease(windows);
            }
        }
        finally
        {
            NativeMethods.CFRelease(app);
        }
    }

    // CoreGraphics and Accessibility give a window the same frame in the same global coordinates; a point either way
    private static bool IsAt(nint window, nint positionKey, nint sizeKey, ScreenBounds bounds)
    {
        if (Point(window, positionKey, NativeMethods.KAXValueCGPointType) is not { } at || Point(window, sizeKey, NativeMethods.KAXValueCGSizeType) is not { } size)
            return false;
        return Math.Abs(at.X - bounds.X) <= 1 && Math.Abs(at.Y - bounds.Y) <= 1 && Math.Abs(size.X - bounds.Width) <= 1 && Math.Abs(size.Y - bounds.Height) <= 1;
    }

    // a CGPoint or CGSize attribute, which share a layout
    private static CGPoint? Point(nint element, nint key, int type)
    {
        if (NativeMethods.AXUIElementCopyAttributeValue(element, key, out var value) != NativeMethods.KAXErrorSuccess || value == 0) return null;
        try
        {
            return NativeMethods.AXValueGetValue(value, type, out var point) ? point : null;
        }
        finally
        {
            NativeMethods.CFRelease(value);
        }
    }
}

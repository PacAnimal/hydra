using Hydra.FileTransfer;
using Microsoft.Extensions.Logging;

namespace Hydra.Platform.MacOs;

// resolves the paste destination by querying the frontmost application's focused Finder window
// via the Accessibility API — not the element under the cursor.
// requires Hydra to have Accessibility permission (already needed for input capture).
public sealed class MacDropTargetResolver : IDropTargetResolver
{
    private readonly ILogger<MacDropTargetResolver> _log;

    // cached CFStrings for frequently-queried AX attributes
    private static readonly nint CfAxFocusedWindow = NativeMethods.MakeNsString("AXFocusedWindow");
    private static readonly nint CfAxDocument = NativeMethods.MakeNsString("AXDocument");

    public MacDropTargetResolver(ILogger<MacDropTargetResolver> log)
    {
        _log = log;
        NativeMethods.EnsureAppKitLoaded();
        NativeMethods.EnsureApplicationServicesLoaded();
    }

    public string? GetPasteDirectory()
    {
        using var pool = new ObjcAutoreleasePool();
        try
        {
            return GetFolderForFrontmostApp();
        }
        catch (Exception ex)
        {
            _log.LogDebug(ex, "GetPasteDirectory failed");
            return null;
        }
    }

    public void MoveToDestination(string tempDir, string destDir) => FileUtils.MoveTo(tempDir, destDir);

    private static string? GetFolderForFrontmostApp()
    {
        var pid = GetFrontmostPid(out var isFinder);
        if (pid <= 0) return null;

        var axApp = NativeMethods.AXUIElementCreateApplication(pid);
        if (axApp == nint.Zero) return null;
        try
        {
            var axErr = NativeMethods.AXUIElementCopyAttributeValue(axApp, CfAxFocusedWindow, out var window);
            if (axErr != 0 || window == nint.Zero)
                return isFinder ? Environment.GetFolderPath(Environment.SpecialFolder.Desktop) : null;
            try
            {
                var doc = GetWindowDocumentPath(window);
                if (doc != null) return doc;
                // finder window with no AXDocument → desktop is active
                return isFinder ? Environment.GetFolderPath(Environment.SpecialFolder.Desktop) : null;
            }
            finally { NativeMethods.CFRelease(window); }
        }
        finally { NativeMethods.CFRelease(axApp); }
    }

    // returns pid of the frontmost app; sets isFinder=true if it's com.apple.finder
    private static int GetFrontmostPid(out bool isFinder)
    {
        isFinder = false;
        var wsClass = NativeMethods.objc_getClass("NSWorkspace");
        var ws = NativeMethods.objc_msgSend_noarg(wsClass, NativeMethods.sel_registerName("sharedWorkspace"));
        if (ws == nint.Zero) return 0;
        var app = NativeMethods.objc_msgSend_noarg(ws, NativeMethods.sel_registerName("frontmostApplication"));
        if (app == nint.Zero) return 0;
        var bundleIdStr = NativeMethods.objc_msgSend_noarg(app, NativeMethods.sel_registerName("bundleIdentifier"));
        isFinder = NativeMethods.CfStringToManaged(bundleIdStr) == "com.apple.finder";
        return (int)NativeMethods.objc_msgSend_long(app, NativeMethods.sel_registerName("processIdentifier"));
    }

    private static string? GetWindowDocumentPath(nint windowElement)
    {
        // AXDocument gives the URL of the folder displayed in the window
        var axResult = NativeMethods.AXUIElementCopyAttributeValue(windowElement, CfAxDocument, out var urlValue);
        if (axResult != 0 || urlValue == nint.Zero) return null;
        try
        {
            return FileUtils.FileUrlToLocalPath(NativeMethods.CfStringToManaged(urlValue));
        }
        finally { NativeMethods.CFRelease(urlValue); }
    }
}

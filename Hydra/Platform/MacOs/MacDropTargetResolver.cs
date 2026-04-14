using Hydra.FileTransfer;
using Microsoft.Extensions.Logging;

namespace Hydra.Platform.MacOs;

// resolves the folder under the cursor by using the Accessibility API to find the
// window (Finder or any file-manager) directly under the physical cursor position.
// requires Hydra to have Accessibility permission (already needed for input capture).
public sealed class MacDropTargetResolver : IDropTargetResolver
{
    private readonly ILogger<MacDropTargetResolver> _log;

    // cached CFStrings for frequently-queried AX attributes
    private static readonly nint CfAxWindow = NativeMethods.MakeNsString("AXWindow");
    private static readonly nint CfAxDocument = NativeMethods.MakeNsString("AXDocument");

    public MacDropTargetResolver(ILogger<MacDropTargetResolver> log)
    {
        _log = log;
        NativeMethods.EnsureAppKitLoaded();
        NativeMethods.EnsureApplicationServicesLoaded();
    }

    public string? GetDirectoryUnderCursor()
    {
        using var pool = new ObjcAutoreleasePool();
        try
        {
            return GetFolderUnderCursor();
        }
        catch (Exception ex)
        {
            _log.LogDebug(ex, "GetDirectoryUnderCursor failed");
            return null;
        }
    }

    public void MoveToDestination(string tempDir, string destDir) => FileUtils.MoveTo(tempDir, destDir);

    private static string? GetFolderUnderCursor()
    {
        // get cursor position in CG screen coordinates
        var cgEvent = NativeMethods.CGEventCreate(nint.Zero);
        if (cgEvent == nint.Zero) return null;
        var pos = NativeMethods.CGEventGetLocation(cgEvent);
        NativeMethods.CFRelease(cgEvent);

        // find the AX element under the cursor across all applications
        var sysWide = NativeMethods.AXUIElementCreateSystemWide();
        if (sysWide == nint.Zero) return null;
        try
        {
            var axErr = NativeMethods.AXUIElementCopyElementAtPosition(sysWide, (float)pos.X, (float)pos.Y, out var element);
            if (axErr != 0 || element == nint.Zero) return null;
            try
            {
                return GetElementDocumentPath(element);
            }
            finally { NativeMethods.CFRelease(element); }
        }
        finally { NativeMethods.CFRelease(sysWide); }
    }

    private static string? GetElementDocumentPath(nint element)
    {
        // walk up to the containing AXWindow
        var axErr = NativeMethods.AXUIElementCopyAttributeValue(element, CfAxWindow, out var window);
        if (axErr != 0 || window == nint.Zero) return null;
        try
        {
            return GetWindowDocumentPath(window);
        }
        finally { NativeMethods.CFRelease(window); }
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

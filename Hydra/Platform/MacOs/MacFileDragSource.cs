using Hydra.FileTransfer;
using Microsoft.Extensions.Logging;

namespace Hydra.Platform.MacOs;

public sealed class MacFileDragSource : IFileDragSource
{
    private static readonly nint SelCount = NativeMethods.sel_registerName("count");
    private static readonly nint SelObjectAtIndex = NativeMethods.sel_registerName("objectAtIndex:");
    private static readonly nint SelPropertyListForType = NativeMethods.sel_registerName("propertyListForType:");
    private static readonly nint SelPasteboardItems = NativeMethods.sel_registerName("pasteboardItems");
    private static readonly nint SelPasteboardWithName = NativeMethods.sel_registerName("pasteboardWithName:");
    private static readonly nint SelPressedMouseButtons = NativeMethods.sel_registerName("pressedMouseButtons");
    private static readonly nint SelStringForType = NativeMethods.sel_registerName("stringForType:");
    private static readonly nint StrNsFilenamesPboardType = NativeMethods.MakeNsString("NSFilenamesPboardType");
    // "Apple CFPasteboard drag" is the internal name of the system drag pasteboard, equivalent to
    // [NSPasteboard pasteboardWithName:NSDragPboard]. Private API but stable across macOS versions.
    private static readonly nint StrDragPasteboardName = NativeMethods.MakeNsString("Apple CFPasteboard drag");
    private static readonly nint StrPublicFileUrl = NativeMethods.MakeNsString("public.file-url");

    private readonly ILogger<MacFileDragSource> _log;

    public MacFileDragSource(ILogger<MacFileDragSource> log)
    {
        _log = log;
        NativeMethods.EnsureAppKitLoaded();
    }

    public List<string>? GetDraggedPaths()
    {
        using var pool = new ObjcAutoreleasePool();
        try
        {
            return GetDraggedPathsInner();
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "Failed to read drag pasteboard");
            return null;
        }
    }

    private static List<string>? GetDraggedPathsInner()
    {
        // don't read stale pasteboard data — only proceed if the left mouse button is actually held
        var nsEvent = NativeMethods.objc_getClass("NSEvent");
        if (nsEvent == nint.Zero || (NativeMethods.objc_msgSend_long(nsEvent, SelPressedMouseButtons) & 1) == 0)
            return null;

        var pboard = GetDragPasteboard();
        if (pboard == nint.Zero) return null;

        // try NSFilenamesPboardType first — Finder and most apps still populate it
        var nsArray = NativeMethods.objc_msgSend(pboard, SelPropertyListForType, StrNsFilenamesPboardType);
        if (nsArray != nint.Zero)
        {
            var paths = ReadNsStringArray(nsArray);
            if (paths != null) return paths;
        }

        // fall back to public.file-url items (modern drag API)
        return GetPathsFromFileUrls(pboard);
    }

    private static nint GetDragPasteboard()
    {
        var cls = NativeMethods.objc_getClass("NSPasteboard");
        if (cls == nint.Zero) return nint.Zero;
        return NativeMethods.objc_msgSend(cls, SelPasteboardWithName, StrDragPasteboardName);
    }

    private static List<string>? ReadNsStringArray(nint nsArray)
    {
        var count = (int)NativeMethods.objc_msgSend_long(nsArray, SelCount);
        if (count <= 0) return null;

        var result = new List<string>(count);
        for (var i = 0; i < count; i++)
        {
            var item = NativeMethods.objc_msgSend_nuint(nsArray, SelObjectAtIndex, (nuint)i);
            if (item == nint.Zero) continue;
            var s = NativeMethods.CfStringToManaged(item);
            if (s != null) result.Add(s);
        }
        return result.Count > 0 ? result : null;
    }

    private static List<string>? GetPathsFromFileUrls(nint pboard)
    {
        var items = NativeMethods.objc_msgSend_noarg(pboard, SelPasteboardItems);
        if (items == nint.Zero) return null;

        var count = (int)NativeMethods.objc_msgSend_long(items, SelCount);
        if (count <= 0) return null;

        var result = new List<string>(count);
        for (var i = 0; i < count; i++)
        {
            var item = NativeMethods.objc_msgSend_nuint(items, SelObjectAtIndex, (nuint)i);
            if (item == nint.Zero) continue;
            var nsUrlStr = NativeMethods.objc_msgSend(item, SelStringForType, StrPublicFileUrl);
            if (nsUrlStr == nint.Zero) continue;
            var localPath = FileUtils.FileUrlToLocalPath(NativeMethods.CfStringToManaged(nsUrlStr));
            if (localPath != null) result.Add(localPath);
        }
        return result.Count > 0 ? result : null;
    }

}

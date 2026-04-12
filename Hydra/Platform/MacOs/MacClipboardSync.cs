using System.Runtime.InteropServices;

namespace Hydra.Platform.MacOs;

public sealed class MacClipboardSync : IClipboardSync
{
    private const string PasteboardTypeString = "public.utf8-plain-text";
    private const string PasteboardTypePng = "public.png";

    public bool SupportsFiles => true;

    private string? _lastSetText;
    private ulong? _lastSetImageHash;
    private HashSet<string>? _lastSetFilePaths;
    private string? _storedPrimaryText;

    public MacClipboardSync()
    {
        // NSPasteboard lives in AppKit — must be loaded before objc_getClass can find it.
        // Slaves don't open an event tap, so AppKit may not be loaded otherwise.
        NativeLibrary.Load("/System/Library/Frameworks/AppKit.framework/AppKit");
    }

    public string? GetText()
    {
        var pasteboard = GetGeneralPasteboard();
        if (pasteboard == nint.Zero) return null;

        var typeStr = MakeNsString(PasteboardTypeString);
        var sel = NativeMethods.sel_registerName("stringForType:");
        var result = NativeMethods.objc_msgSend(pasteboard, sel, typeStr);
        NativeMethods.CFRelease(typeStr);

        if (result == nint.Zero) return null;
        var text = NsStringToManaged(result);
        return text == _lastSetText ? null : text;
    }

    public void SetText(string text)
    {
        _lastSetText = text;

        var pasteboard = GetGeneralPasteboard();
        if (pasteboard == nint.Zero) return;

        var clearSel = NativeMethods.sel_registerName("clearContents");
        NativeMethods.objc_msgSend_noarg(pasteboard, clearSel);
        WriteText(pasteboard, text);
    }

    public string? GetPrimaryText() => _storedPrimaryText;

    public void SetPrimaryText(string text) => _storedPrimaryText = text;

    public byte[]? GetImagePng()
    {
        var pasteboard = GetGeneralPasteboard();
        if (pasteboard == nint.Zero) return null;

        var typeStr = MakeNsString(PasteboardTypePng);
        var sel = NativeMethods.sel_registerName("dataForType:");
        var nsData = NativeMethods.objc_msgSend(pasteboard, sel, typeStr);
        NativeMethods.CFRelease(typeStr);

        if (nsData == nint.Zero) return null;

        var length = NativeMethods.CFDataGetLength(nsData);
        if (length <= 0) return null;

        var ptr = NativeMethods.CFDataGetBytePtr(nsData);
        if (ptr == nint.Zero) return null;

        var bytes = new byte[(int)length];
        Marshal.Copy(ptr, bytes, 0, (int)length);

        // suppress echo: don't return data we just wrote
        if (_lastSetImageHash.HasValue && ClipboardUtils.QuickHash(bytes) == _lastSetImageHash.Value)
            return null;

        return bytes;
    }

    public void SetImagePng(byte[] pngData)
    {
        _lastSetImageHash = ClipboardUtils.QuickHash(pngData);

        var pasteboard = GetGeneralPasteboard();
        if (pasteboard == nint.Zero) return;

        var clearSel = NativeMethods.sel_registerName("clearContents");
        NativeMethods.objc_msgSend_noarg(pasteboard, clearSel);
        WriteImagePng(pasteboard, pngData);
    }

    public void SetClipboard(string? text, string? primaryText, byte[]? imagePng, List<TempFileEntry>? files = null)
    {
        if (text == null && primaryText == null && imagePng == null && files == null) return;

        if (text != null) _lastSetText = text;
        if (primaryText != null) _storedPrimaryText = primaryText;
        if (imagePng != null) _lastSetImageHash = ClipboardUtils.QuickHash(imagePng);
        if (files != null) _lastSetFilePaths = files.ToPathSet();

        var pasteboard = GetGeneralPasteboard();
        if (pasteboard == nint.Zero) return;

        // single clear, then write everything atomically
        var clearSel = NativeMethods.sel_registerName("clearContents");
        NativeMethods.objc_msgSend_noarg(pasteboard, clearSel);

        if (text != null) WriteText(pasteboard, text);
        if (imagePng != null) WriteImagePng(pasteboard, imagePng);
        if (files != null) WritePasteboardFileList(pasteboard, files.ToPaths());
    }

    public List<string>? GetFilePaths()
    {
        var pasteboard = GetGeneralPasteboard();
        if (pasteboard == nint.Zero) return null;

        var paths = new List<string>();

        // try NSFilenamesPboardType first: returns an NSArray of path strings directly
        var legacyType = MakeNsString("NSFilenamesPboardType");
        var propListSel = NativeMethods.sel_registerName("propertyListForType:");
        var propList = NativeMethods.objc_msgSend(pasteboard, propListSel, legacyType);
        NativeMethods.CFRelease(legacyType);

        if (propList != nint.Zero)
        {
            var countSel = NativeMethods.sel_registerName("count");
            var atIndexSel = NativeMethods.sel_registerName("objectAtIndex:");
            var count = NativeMethods.objc_msgSend_long(propList, countSel);
            for (long i = 0; i < count; i++)
            {
                var item = NativeMethods.objc_msgSend_nuint(propList, atIndexSel, (nuint)i);
                var path = NsStringToManaged(item);
                if (path != null) paths.Add(path);
            }
        }

        // fall back to pasteboardItems → public.file-url (modern non-Finder apps)
        if (paths.Count == 0)
        {
            var itemsSel = NativeMethods.sel_registerName("pasteboardItems");
            var items = NativeMethods.objc_msgSend_noarg(pasteboard, itemsSel);
            if (items != nint.Zero)
            {
                var countSel = NativeMethods.sel_registerName("count");
                var atIndexSel = NativeMethods.sel_registerName("objectAtIndex:");
                var stringForTypeSel = NativeMethods.sel_registerName("stringForType:");
                var fileUrlType = MakeNsString("public.file-url");
                var count = NativeMethods.objc_msgSend_long(items, countSel);
                for (long i = 0; i < count; i++)
                {
                    var item = NativeMethods.objc_msgSend_nuint(items, atIndexSel, (nuint)i);
                    if (item == nint.Zero) continue;
                    var urlNsStr = NativeMethods.objc_msgSend(item, stringForTypeSel, fileUrlType);
                    if (urlNsStr == nint.Zero) continue;
                    var urlStr = NsStringToManaged(urlNsStr);
                    if (urlStr == null) continue;
                    if (Uri.TryCreate(urlStr, UriKind.Absolute, out var uri) && uri.IsFile)
                        paths.Add(uri.LocalPath);
                }
                NativeMethods.CFRelease(fileUrlType);
            }
        }

        if (paths.Count == 0) return null;

        // echo suppression: return null if these are the same paths we just set
        if (_lastSetFilePaths != null && paths.Count == _lastSetFilePaths.Count && paths.All(p => _lastSetFilePaths.Contains(p)))
            return null;

        return paths;
    }

    private static void WritePasteboardFileList(nint pasteboard, List<string> paths)
    {
        // build an NSMutableArray of NSString paths for NSFilenamesPboardType
        var nsArrayClass = NativeMethods.objc_getClass("NSMutableArray");
        var arraySel = NativeMethods.sel_registerName("array");
        var array = NativeMethods.objc_msgSend_noarg(nsArrayClass, arraySel);
        if (array == nint.Zero) return;

        var typeStr = nint.Zero;
        try
        {
            var addSel = NativeMethods.sel_registerName("addObject:");
            foreach (var path in paths)
            {
                var nsPath = MakeNsString(path);
                try { NativeMethods.objc_msgSend(array, addSel, nsPath); }
                finally { NativeMethods.CFRelease(nsPath); }
            }

            typeStr = MakeNsString("NSFilenamesPboardType");
            var setPropListSel = NativeMethods.sel_registerName("setPropertyList:forType:");
            NativeMethods.objc_msgSend_2arg(pasteboard, setPropListSel, array, typeStr);
        }
        finally
        {
            if (typeStr != nint.Zero) NativeMethods.CFRelease(typeStr);
            NativeMethods.CFRelease(array);
        }
    }

    private static void WriteText(nint pasteboard, string text)
    {
        var nsStr = MakeNsString(text);
        var typeStr = MakeNsString(PasteboardTypeString);
        var setSel = NativeMethods.sel_registerName("setString:forType:");
        NativeMethods.objc_msgSend_2arg(pasteboard, setSel, nsStr, typeStr);
        NativeMethods.CFRelease(nsStr);
        NativeMethods.CFRelease(typeStr);
    }

    private static unsafe void WriteImagePng(nint pasteboard, byte[] pngData)
    {
        var nsDataClass = NativeMethods.objc_getClass("NSData");
        var dataSel = NativeMethods.sel_registerName("dataWithBytes:length:");
        nint nsData;
        fixed (byte* ptr = pngData)
            nsData = NativeMethods.objc_msgSend_ptr_nuint(nsDataClass, dataSel, ptr, (nuint)pngData.Length);
        if (nsData == nint.Zero) return;

        var typeStr = MakeNsString(PasteboardTypePng);
        var setSel = NativeMethods.sel_registerName("setData:forType:");
        NativeMethods.objc_msgSend_2arg(pasteboard, setSel, nsData, typeStr);
        NativeMethods.CFRelease(typeStr);
    }

    private static nint GetGeneralPasteboard()
    {
        var cls = NativeMethods.objc_getClass("NSPasteboard");
        if (cls == nint.Zero) return nint.Zero;
        var sel = NativeMethods.sel_registerName("generalPasteboard");
        return NativeMethods.objc_msgSend_noarg(cls, sel);
    }

    // CFStringCreateWithCString is toll-free bridged to NSString
    private static nint MakeNsString(string s)
        => NativeMethods.CFStringCreateWithCString(nint.Zero, s, NativeMethods.KCFStringEncodingUtf8);

    private static unsafe string? NsStringToManaged(nint nsStr)
    {
        if (nsStr == nint.Zero) return null;

        // get UTF-16 char count, then compute worst-case UTF-8 byte count (4 bytes per char + null)
        var lenSel = NativeMethods.sel_registerName("length");
        var charCount = NativeMethods.objc_msgSend_long(nsStr, lenSel);
        var bufSize = (nint)(charCount * 4 + 1);

        var buf = Marshal.AllocHGlobal(bufSize);
        try
        {
            return NativeMethods.CFStringGetCString(nsStr, (byte*)buf, bufSize, NativeMethods.KCFStringEncodingUtf8)
                ? Marshal.PtrToStringUTF8(buf)
                : null;
        }
        finally
        {
            Marshal.FreeHGlobal(buf);
        }
    }
}

using System.Runtime.InteropServices;
using Microsoft.Extensions.Logging;

namespace Hydra.Platform.MacOs;

public sealed class MacClipboardSync : IClipboardSync
{
    private const string PasteboardTypeString = "public.utf8-plain-text";
    private const string PasteboardTypePng = "public.png";
    private const string PasteboardTypeHtml = "public.html";
    private const string PasteboardTypeRtf = "public.rtf";
    private const string PasteboardTypeFileUrl = "public.file-url";
    private const string PasteboardTypeLegacyFileNames = "NSFilenamesPboardType";

    private readonly ILogger<MacClipboardSync> _log;
    private ClipboardEchoFilter _echo;
    private string? _storedPrimaryText;
    private long _ownedChangeCount = -1;

    public MacClipboardSync(ILogger<MacClipboardSync> log)
    {
        _log = log;
        // NSPasteboard lives in AppKit — must be loaded before objc_getClass can find it.
        // Slaves don't open an event tap, so AppKit may not be loaded otherwise.
        NativeMethods.EnsureAppKitLoaded();
    }

    public string? GetText()
    {
        using var pool = new ObjcAutoreleasePool();
        try
        {
            return GetTextInner();
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "Failed to read clipboard text");
            return null;
        }
    }

    private string? GetTextInner()
    {
        var pasteboard = GetGeneralPasteboard();
        if (pasteboard == nint.Zero) return null;

        var typeStr = NativeMethods.MakeNsString(PasteboardTypeString);
        var sel = NativeMethods.sel_registerName("stringForType:");
        var result = NativeMethods.objc_msgSend(pasteboard, sel, typeStr);
        NativeMethods.CFRelease(typeStr);

        if (result == nint.Zero) return null;
        var text = NativeMethods.CfStringToManaged(result);
        return OwnsCurrentClipboard(pasteboard) ? _echo.FilterText(text) : text;
    }

    public void SetText(string text)
    {
        _echo.TrackText(text);

        using var pool = new ObjcAutoreleasePool();
        var pasteboard = GetGeneralPasteboard();
        if (pasteboard == nint.Zero) return;

        var clearSel = NativeMethods.sel_registerName("clearContents");
        var changeCount = NativeMethods.objc_msgSend_long(pasteboard, clearSel);
        WriteText(pasteboard, text);
        Volatile.Write(ref _ownedChangeCount, changeCount);
    }

    public string? GetPrimaryText()
    {
        var pasteboard = GetGeneralPasteboard();
        return pasteboard != nint.Zero && OwnsCurrentClipboard(pasteboard) ? _storedPrimaryText : null;
    }

    public void SetPrimaryText(string text) => _storedPrimaryText = text;

    public byte[]? GetImagePng()
    {
        using var pool = new ObjcAutoreleasePool();
        try
        {
            return GetImagePngInner();
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "Failed to read clipboard image");
            return null;
        }
    }

    private byte[]? GetImagePngInner()
    {
        var pasteboard = GetGeneralPasteboard();
        if (pasteboard == nint.Zero) return null;

        var typeStr = NativeMethods.MakeNsString(PasteboardTypePng);
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

        if (OwnsCurrentClipboard(pasteboard) && _echo.IsDuplicateImage(bytes)) return null;

        return bytes;
    }

    public void SetImagePng(byte[] pngData)
    {
        _echo.TrackImage(pngData);

        using var pool = new ObjcAutoreleasePool();
        var pasteboard = GetGeneralPasteboard();
        if (pasteboard == nint.Zero) return;

        var clearSel = NativeMethods.sel_registerName("clearContents");
        var changeCount = NativeMethods.objc_msgSend_long(pasteboard, clearSel);
        WriteImagePng(pasteboard, pngData);
        Volatile.Write(ref _ownedChangeCount, changeCount);
    }

    public string? GetHtml()
    {
        using var pool = new ObjcAutoreleasePool();
        try
        {
            var pasteboard = GetGeneralPasteboard();
            if (pasteboard == nint.Zero) return null;

            var typeStr = NativeMethods.MakeNsString(PasteboardTypeHtml);
            var sel = NativeMethods.sel_registerName("stringForType:");
            var result = NativeMethods.objc_msgSend(pasteboard, sel, typeStr);
            NativeMethods.CFRelease(typeStr);

            if (result == nint.Zero) return null;
            var html = NativeMethods.CfStringToManaged(result);
            return OwnsCurrentClipboard(pasteboard) ? _echo.FilterHtml(html) : html;
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "Failed to read clipboard html");
            return null;
        }
    }

    public byte[]? GetRtf()
    {
        using var pool = new ObjcAutoreleasePool();
        try
        {
            var pasteboard = GetGeneralPasteboard();
            if (pasteboard == nint.Zero) return null;

            var typeStr = NativeMethods.MakeNsString(PasteboardTypeRtf);
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
            return OwnsCurrentClipboard(pasteboard) ? _echo.FilterRtf(bytes) : bytes;
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "Failed to read clipboard rtf");
            return null;
        }
    }

    public void SetClipboard(ClipboardSnapshot contents)
    {
        var text = contents.Text;
        var primaryText = contents.PrimaryText;
        var imagePng = contents.ImagePng;
        var html = contents.Html;
        var rtf = contents.Rtf;
        using var pool = new ObjcAutoreleasePool();
        try
        {
            if (text == null && primaryText == null && imagePng == null && html == null && rtf == null) return;

            if (text != null) _echo.TrackText(text);
            if (primaryText != null) _storedPrimaryText = primaryText;
            if (imagePng != null) _echo.TrackImage(imagePng);
            if (html != null) _echo.TrackHtml(html);
            if (rtf != null) _echo.TrackRtf(rtf);

            var pasteboard = GetGeneralPasteboard();
            if (pasteboard == nint.Zero) return;

            // single clear, then write every representation atomically
            var clearSel = NativeMethods.sel_registerName("clearContents");
            var changeCount = NativeMethods.objc_msgSend_long(pasteboard, clearSel);

            if (text != null) WriteText(pasteboard, text);
            if (html != null) WriteHtml(pasteboard, html);
            if (rtf != null) WriteRtf(pasteboard, rtf);
            if (imagePng != null) WriteImagePng(pasteboard, imagePng);
            Volatile.Write(ref _ownedChangeCount, changeCount);
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "Failed to write clipboard");
        }
    }

    public bool HasFileClipboard()
    {
        using var pool = new ObjcAutoreleasePool();
        try
        {
            var pasteboard = GetGeneralPasteboard();
            if (pasteboard == nint.Zero) return false;
            var types = NativeMethods.objc_msgSend_noarg(pasteboard, NativeMethods.sel_registerName("types"));
            if (types == nint.Zero) return false;

            var count = NativeMethods.objc_msgSend_long(types, NativeMethods.sel_registerName("count"));
            var objectAtIndex = NativeMethods.sel_registerName("objectAtIndex:");
            for (long i = 0; i < count; i++)
            {
                var type = NativeMethods.objc_msgSend_nuint(types, objectAtIndex, (nuint)i);
                if (type == nint.Zero) continue;
                var name = NativeMethods.CfStringToManaged(type);
                if (name is PasteboardTypeFileUrl or PasteboardTypeLegacyFileNames) return true;
            }
            return false;
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "Failed to inspect clipboard types");
            return false;
        }
    }

    public bool CanUseEchoFallback()
    {
        var pasteboard = GetGeneralPasteboard();
        return pasteboard != nint.Zero && OwnsCurrentClipboard(pasteboard);
    }

    private bool OwnsCurrentClipboard(nint pasteboard)
    {
        var owned = Volatile.Read(ref _ownedChangeCount);
        if (owned < 0) return false;
        return NativeMethods.objc_msgSend_long(pasteboard, NativeMethods.sel_registerName("changeCount")) == owned;
    }

    private static void WriteText(nint pasteboard, string text)
    {
        var nsStr = NativeMethods.MakeNsString(text);
        var typeStr = NativeMethods.MakeNsString(PasteboardTypeString);
        var setSel = NativeMethods.sel_registerName("setString:forType:");
        NativeMethods.objc_msgSend_2arg(pasteboard, setSel, nsStr, typeStr);
        NativeMethods.CFRelease(nsStr);
        NativeMethods.CFRelease(typeStr);
    }

    private static void WriteHtml(nint pasteboard, string html)
    {
        var nsStr = NativeMethods.MakeNsString(html);
        var typeStr = NativeMethods.MakeNsString(PasteboardTypeHtml);
        var setSel = NativeMethods.sel_registerName("setString:forType:");
        NativeMethods.objc_msgSend_2arg(pasteboard, setSel, nsStr, typeStr);
        NativeMethods.CFRelease(nsStr);
        NativeMethods.CFRelease(typeStr);
    }

    private static unsafe void WriteRtf(nint pasteboard, byte[] rtf)
    {
        var nsDataClass = NativeMethods.objc_getClass("NSData");
        var dataSel = NativeMethods.sel_registerName("dataWithBytes:length:");
        nint nsData;
        fixed (byte* ptr = rtf)
            nsData = NativeMethods.objc_msgSend_ptr_nuint(nsDataClass, dataSel, ptr, (nuint)rtf.Length);
        if (nsData == nint.Zero) return;

        var typeStr = NativeMethods.MakeNsString(PasteboardTypeRtf);
        var setSel = NativeMethods.sel_registerName("setData:forType:");
        NativeMethods.objc_msgSend_2arg(pasteboard, setSel, nsData, typeStr);
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

        var typeStr = NativeMethods.MakeNsString(PasteboardTypePng);
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

}

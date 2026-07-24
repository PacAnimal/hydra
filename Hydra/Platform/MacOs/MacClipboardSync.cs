using System.Runtime.InteropServices;
using Microsoft.Extensions.Logging;

namespace Hydra.Platform.MacOs;

public sealed class MacClipboardSync : IClipboardSync
{
    private const string PasteboardTypeString = "public.utf8-plain-text";
    private const string PasteboardTypePng = "public.png";
    private const string PasteboardTypeHtml = "public.html";
    private const string PasteboardTypeRtf = "public.rtf";

    private readonly ILogger<MacClipboardSync> _log;
    private ClipboardEchoFilter _echo;
    private string? _storedPrimaryText;

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
        return _echo.FilterText(text);
    }

    public void SetText(string text)
    {
        _echo.TrackText(text);

        using var pool = new ObjcAutoreleasePool();
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

        if (_echo.IsDuplicateImage(bytes)) return null;

        return bytes;
    }

    public void SetImagePng(byte[] pngData)
    {
        _echo.TrackImage(pngData);

        using var pool = new ObjcAutoreleasePool();
        var pasteboard = GetGeneralPasteboard();
        if (pasteboard == nint.Zero) return;

        var clearSel = NativeMethods.sel_registerName("clearContents");
        NativeMethods.objc_msgSend_noarg(pasteboard, clearSel);
        WriteImagePng(pasteboard, pngData);
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
            return _echo.FilterHtml(NativeMethods.CfStringToManaged(result));
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
            return _echo.FilterRtf(bytes);
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
            NativeMethods.objc_msgSend_noarg(pasteboard, clearSel);

            if (text != null) WriteText(pasteboard, text);
            if (html != null) WriteHtml(pasteboard, html);
            if (rtf != null) WriteRtf(pasteboard, rtf);
            if (imagePng != null) WriteImagePng(pasteboard, imagePng);
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "Failed to write clipboard");
        }
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

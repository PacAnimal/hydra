using System.Runtime.InteropServices;

namespace Hydra.Platform.MacOs;

public sealed class MacClipboardSync : IClipboardSync
{
    private const string PasteboardTypeString = "public.utf8-plain-text";

    private string? _lastSetText;

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

        var nsStr = MakeNsString(text);
        var typeStr = MakeNsString(PasteboardTypeString);
        var setSel = NativeMethods.sel_registerName("setString:forType:");
        NativeMethods.objc_msgSend_2arg(pasteboard, setSel, nsStr, typeStr);
        NativeMethods.CFRelease(nsStr);
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

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
    private const string PasteboardTypeConcealed = "org.nspasteboard.ConcealedType";
    private const string PasteboardTypeTransient = "org.nspasteboard.TransientType";

    // the formats sent as files, never as their names, and those whose text is no text copy of the user's
    private static readonly string[] FileTypes = [PasteboardTypeFileUrl, PasteboardTypeLegacyFileNames];
    private static readonly string[] NotTextCopyTypes = [PasteboardTypePng, .. FileTypes, PasteboardTypeConcealed, PasteboardTypeTransient];

    private readonly ILogger<MacClipboardSync> _log;
    private ClipboardEchoFilter _echo;
    private readonly CopiedPrimary _primary;
    private long _ownedChangeCount = -1;
    private readonly string? _pasteboardName;

    public MacClipboardSync(ILogger<MacClipboardSync> log) : this(log, null) { }

    // a named pasteboard for tests, which leave the user's general one alone
    internal MacClipboardSync(ILogger<MacClipboardSync> log, string? pasteboardName)
    {
        _log = log;
        _pasteboardName = pasteboardName;
        _primary = new CopiedPrimary(() => ChangeCount(GetGeneralPasteboard()), () => ReadTextCopy(GetGeneralPasteboard()), log);
        // NSPasteboard lives in AppKit — must be loaded before objc_getClass can find it.
        // Slaves don't open an event tap, so AppKit may not be loaded otherwise.
        NativeHelpers.EnsureAppKitLoaded();
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

        var text = ReadText(pasteboard);
        return OwnsCurrentClipboard(pasteboard) ? _echo.FilterText(text) : text;
    }

    private static string? ReadText(nint pasteboard)
    {
        var typeStr = NativeHelpers.MakeNsString(PasteboardTypeString);
        var sel = NativeMethods.sel_registerName("stringForType:");
        var result = NativeMethods.objc_msgSend(pasteboard, sel, typeStr);
        NativeMethods.CFRelease(typeStr);
        return result == nint.Zero ? null : NativeHelpers.CfStringToManaged(result);
    }

    // CopiedPrimary's readTextCopy, by pasteboard type
    private static string? ReadTextCopy(nint pasteboard) =>
        PasteboardTypes(pasteboard).Any(t => NotTextCopyTypes.Contains(t)) ? null : ReadText(pasteboard);

    private static long ChangeCount(nint pasteboard) => NativeMethods.objc_msgSend_long(pasteboard, NativeMethods.sel_registerName("changeCount"));

    public string? GetPrimaryText()
    {
        using var pool = new ObjcAutoreleasePool();
        return _primary.Read();
    }

    public bool SetPrimaryText(string text)
    {
        using var pool = new ObjcAutoreleasePool();
        return _primary.Relay(text);
    }

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

        var typeStr = NativeHelpers.MakeNsString(PasteboardTypePng);
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

    public string? GetHtml()
    {
        using var pool = new ObjcAutoreleasePool();
        try
        {
            var pasteboard = GetGeneralPasteboard();
            if (pasteboard == nint.Zero) return null;

            var typeStr = NativeHelpers.MakeNsString(PasteboardTypeHtml);
            var sel = NativeMethods.sel_registerName("stringForType:");
            var result = NativeMethods.objc_msgSend(pasteboard, sel, typeStr);
            NativeMethods.CFRelease(typeStr);

            if (result == nint.Zero) return null;
            var html = NativeHelpers.CfStringToManaged(result);
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

            var typeStr = NativeHelpers.MakeNsString(PasteboardTypeRtf);
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

    public bool SetClipboard(ClipboardSnapshot contents)
    {
        using var pool = new ObjcAutoreleasePool();
        return _primary.Write(contents.PrimaryText, leftAt =>
        {
            var pasteboard = GetGeneralPasteboard();
            if (pasteboard == nint.Zero) return false;
            // single clear, then write every representation atomically; the count the clear leaves is ours, so a copy
            // another app makes after it is still the user's
            var cleared = NativeMethods.objc_msgSend_long(pasteboard, NativeMethods.sel_registerName("clearContents"));
            leftAt(cleared);
            Volatile.Write(ref _ownedChangeCount, cleared);
            _echo.Track(contents);
            ClipboardUtils.WriteAll(_log,
                contents.Text is { } text ? () => WriteString(pasteboard, text, PasteboardTypeString) : null,
                contents.Html is { } html ? () => WriteString(pasteboard, html, PasteboardTypeHtml) : null,
                contents.Rtf is { } rtf ? () => WriteData(pasteboard, rtf, PasteboardTypeRtf) : null,
                contents.ImagePng is { } png ? () => WriteData(pasteboard, png, PasteboardTypePng) : null);
            return true; // the clear replaced it
        });
    }

    public bool HasFileClipboard()
    {
        using var pool = new ObjcAutoreleasePool();
        try
        {
            var pasteboard = GetGeneralPasteboard();
            return pasteboard != nint.Zero && PasteboardTypes(pasteboard).Any(t => FileTypes.Contains(t));
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "Failed to inspect clipboard types");
            return false;
        }
    }

    private static IEnumerable<string?> PasteboardTypes(nint pasteboard)
    {
        var types = NativeMethods.objc_msgSend_noarg(pasteboard, NativeMethods.sel_registerName("types"));
        if (types == nint.Zero) yield break;

        var count = NativeMethods.objc_msgSend_long(types, NativeMethods.sel_registerName("count"));
        var objectAtIndex = NativeMethods.sel_registerName("objectAtIndex:");
        for (long i = 0; i < count; i++)
        {
            var type = NativeMethods.objc_msgSend_nuint(types, objectAtIndex, (nuint)i);
            if (type != nint.Zero) yield return NativeHelpers.CfStringToManaged(type);
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
        return ChangeCount(pasteboard) == owned;
    }

    // setString:forType: and setData:forType: answer whether the pasteboard took it, as a BOOL: only its low byte is set
    private static bool WriteString(nint pasteboard, string value, string type)
    {
        var nsStr = NativeHelpers.MakeNsString(value);
        var typeStr = NativeHelpers.MakeNsString(type);
        var taken = (byte)NativeMethods.objc_msgSend_2arg(pasteboard, NativeMethods.sel_registerName("setString:forType:"), nsStr, typeStr) != 0;
        NativeMethods.CFRelease(nsStr);
        NativeMethods.CFRelease(typeStr);
        return taken;
    }

    private static unsafe bool WriteData(nint pasteboard, byte[] bytes, string type)
    {
        nint nsData;
        fixed (byte* ptr = bytes)
            nsData = NativeMethods.objc_msgSend_ptr_nuint(NativeMethods.objc_getClass("NSData"), NativeMethods.sel_registerName("dataWithBytes:length:"), ptr,
                (nuint)bytes.Length);
        if (nsData == nint.Zero) return false;

        var typeStr = NativeHelpers.MakeNsString(type);
        var taken = (byte)NativeMethods.objc_msgSend_2arg(pasteboard, NativeMethods.sel_registerName("setData:forType:"), nsData, typeStr) != 0;
        NativeMethods.CFRelease(typeStr);
        return taken;
    }

    private nint GetGeneralPasteboard()
    {
        var cls = NativeMethods.objc_getClass("NSPasteboard");
        if (cls == nint.Zero) return nint.Zero;
        if (_pasteboardName == null) return NativeMethods.objc_msgSend_noarg(cls, NativeMethods.sel_registerName("generalPasteboard"));

        var name = NativeHelpers.MakeNsString(_pasteboardName);
        var pasteboard = NativeMethods.objc_msgSend(cls, NativeMethods.sel_registerName("pasteboardWithName:"), name);
        NativeMethods.CFRelease(name);
        return pasteboard;
    }

}

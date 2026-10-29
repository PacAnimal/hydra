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
    private static readonly string[] ConcealedTypes = [PasteboardTypeConcealed, PasteboardTypeTransient];
    private static readonly string[] NotTextCopyTypes = [PasteboardTypePng, .. FileTypes, .. ConcealedTypes];

    private readonly ILogger<MacClipboardSync> _log;
    private readonly ClipboardEchoFilter _echo;
    private readonly CopiedPrimary _primary;
    private readonly string? _pasteboardName;

    public MacClipboardSync(ILogger<MacClipboardSync> log) : this(log, null) { }

    // a named pasteboard for tests, which leave the user's general one alone
    internal MacClipboardSync(ILogger<MacClipboardSync> log, string? pasteboardName)
    {
        _log = log;
        _pasteboardName = pasteboardName;
        _primary = new CopiedPrimary(() => ChangeCount(GetGeneralPasteboard()), () => ReadTextCopy(GetGeneralPasteboard()), log);
        _echo = new ClipboardEchoFilter(_primary.Owns);
        // NSPasteboard lives in AppKit — must be loaded before objc_getClass can find it.
        // Slaves don't open an event tap, so AppKit may not be loaded otherwise.
        NativeHelpers.EnsureAppKitLoaded();
    }

    // tests only: runs between the read's first change count and its last, so a test can copy in the middle of a read,
    // which no pasteboard of its own can time
    internal Action? DuringRead { get; set; }

    // every type in one pool, against one change count: a copy made during the read is read again, once
    public ClipboardRead ReadAll()
    {
        if (Guarded("clipboard", ReadAllOnce) is { } first) return first;
        if (_log.IsEnabled(LogLevel.Debug)) _log.LogDebug("The pasteboard changed while it was read; reading it again");
        return Guarded("clipboard", ReadAllOnce) ?? throw new ClipboardUnreadableException("the pasteboard changed while it was read, twice");
    }

    // null when the pasteboard changed during the read
    private ClipboardRead? ReadAllOnce(nint pasteboard)
    {
        var before = ChangeCount(pasteboard);
        var types = PasteboardTypes(pasteboard).ToList();
        var read = types.Any(t => FileTypes.Contains(t)) ? ClipboardRead.Files : ReadFormats(pasteboard, types);
        DuringRead?.Invoke();
        return ChangeCount(pasteboard) == before ? read : null;
    }

    private ClipboardRead ReadFormats(nint pasteboard, List<string?> types) =>
        new(_echo.FilterText(ReadString(pasteboard, PasteboardTypeString)), _echo.FilterImage(ReadData(pasteboard, PasteboardTypePng)),
            _echo.FilterHtml(ReadString(pasteboard, PasteboardTypeHtml)), _echo.FilterRtf(ReadData(pasteboard, PasteboardTypeRtf)),
            types.Any(t => ConcealedTypes.Contains(t)));

    // one read of the general pasteboard in its own pool; unreadable when there is none or the read fails
    private T Guarded<T>(string what, Func<nint, T> read)
    {
        using var pool = new ObjcAutoreleasePool();
        try
        {
            var pasteboard = GetGeneralPasteboard();
            if (pasteboard != nint.Zero) return read(pasteboard);
        }
        catch (Exception ex)
        {
            throw new ClipboardUnreadableException($"the pasteboard's {what} could not be read", ex);
        }
        throw new ClipboardUnreadableException($"no pasteboard to read the {what} from");
    }

    private static string? ReadString(nint pasteboard, string type)
    {
        using var typeStr = new NsString(type);
        var result = NativeMethods.objc_msgSend(pasteboard, NativeMethods.sel_registerName("stringForType:"), typeStr);
        return result == nint.Zero ? null : NativeHelpers.CfStringToManaged(result);
    }

    private static byte[]? ReadData(nint pasteboard, string type)
    {
        using var typeStr = new NsString(type);
        var nsData = NativeMethods.objc_msgSend(pasteboard, NativeMethods.sel_registerName("dataForType:"), typeStr);
        if (nsData == nint.Zero) return null;

        var length = NativeMethods.CFDataGetLength(nsData);
        var ptr = NativeMethods.CFDataGetBytePtr(nsData);
        if (length <= 0 || ptr == nint.Zero) return null;

        var bytes = new byte[(int)length];
        Marshal.Copy(ptr, bytes, 0, (int)length);
        return bytes;
    }

    // CopiedPrimary's readTextCopy, by pasteboard type
    private static string? ReadTextCopy(nint pasteboard) =>
        PasteboardTypes(pasteboard).Any(t => NotTextCopyTypes.Contains(t)) ? null : ReadString(pasteboard, PasteboardTypeString);

    private static long ChangeCount(nint pasteboard) => NativeMethods.objc_msgSend_long(pasteboard, NativeMethods.sel_registerName("changeCount"));

    // the same counter CopiedPrimary versions by; null when there is no pasteboard
    public long? ChangeCount()
    {
        using var pool = new ObjcAutoreleasePool();
        var pasteboard = GetGeneralPasteboard();
        return pasteboard == nint.Zero ? null : ChangeCount(pasteboard);
    }

    // the count Hydra's last write left, as the write saw it
    public long? WroteAt() => _primary.WrittenAt();

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

    public ClipboardSnapshot? SetClipboard(ClipboardSnapshot contents)
    {
        using var pool = new ObjcAutoreleasePool();
        ClipboardSnapshot? written = null;
        var replaced = _primary.Write(contents.PrimaryText, leftAt =>
        {
            var pasteboard = GetGeneralPasteboard();
            if (pasteboard == nint.Zero) return false;
            // single clear, then write every representation atomically; the count the clear leaves is ours, so a copy
            // another app makes after it is still the user's
            var cleared = NativeMethods.objc_msgSend_long(pasteboard, NativeMethods.sel_registerName("clearContents"));
            leftAt(cleared);
            var marked = ClipboardUtils.WriteMarkers(_log, contents, () => WriteConcealedMarkers(pasteboard),
                () => WriteData(pasteboard, [], PasteboardTypeTransient));
            _echo.Track(marked);
            written = ClipboardUtils.WriteAll(_log, marked,
                marked.Text is { } text ? new FormatWrite(ClipboardFormat.Text, () => WriteString(pasteboard, text, PasteboardTypeString)) : null,
                marked.Html is { } html ? new FormatWrite(ClipboardFormat.Html, () => WriteString(pasteboard, html, PasteboardTypeHtml)) : null,
                marked.Rtf is { } rtf ? new FormatWrite(ClipboardFormat.Rtf, () => WriteData(pasteboard, rtf, PasteboardTypeRtf)) : null,
                marked.ImagePng is { } png ? new FormatWrite(ClipboardFormat.Image, () => WriteData(pasteboard, png, PasteboardTypePng)) : null);
            return true; // the clear replaced it
        });
        return replaced ? written : null;
    }

    // every marker, even after one is refused
    private static bool WriteConcealedMarkers(nint pasteboard)
    {
        var all = true;
        foreach (var type in ConcealedTypes)
            all &= WriteData(pasteboard, [], type);
        return all;
    }

    // no files when the pasteboard cannot be read: ReadAll says it is unreadable
    public bool HasFileClipboard()
    {
        try
        {
            return Guarded("types", pasteboard => PasteboardTypes(pasteboard).Any(t => FileTypes.Contains(t)));
        }
        catch (ClipboardUnreadableException)
        {
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
        using var pool = new ObjcAutoreleasePool();
        return _primary.Owns();
    }

    // setString:forType: and setData:forType: answer whether the pasteboard took it, as a BOOL: only its low byte is set
    internal static bool WriteString(nint pasteboard, string value, string type)
    {
        using var nsStr = new NsString(value);
        using var typeStr = new NsString(type);
        return (byte)NativeMethods.objc_msgSend_2arg(pasteboard, NativeMethods.sel_registerName("setString:forType:"), nsStr, typeStr) != 0;
    }

    private static unsafe bool WriteData(nint pasteboard, byte[] bytes, string type)
    {
        nint nsData;
        fixed (byte* ptr = bytes)
            nsData = NativeMethods.objc_msgSend_ptr_nuint(NativeMethods.objc_getClass("NSData"), NativeMethods.sel_registerName("dataWithBytes:length:"), ptr,
                (nuint)bytes.Length);
        if (nsData == nint.Zero) return false;

        using var typeStr = new NsString(type);
        return (byte)NativeMethods.objc_msgSend_2arg(pasteboard, NativeMethods.sel_registerName("setData:forType:"), nsData, typeStr) != 0;
    }

    private nint GetGeneralPasteboard() => Pasteboard(_pasteboardName);

    // the general pasteboard, or the named one
    internal static nint Pasteboard(string? named)
    {
        var cls = NativeMethods.objc_getClass("NSPasteboard");
        if (cls == nint.Zero) return nint.Zero;
        if (named == null) return NativeMethods.objc_msgSend_noarg(cls, NativeMethods.sel_registerName("generalPasteboard"));

        using var name = new NsString(named);
        return NativeMethods.objc_msgSend(cls, NativeMethods.sel_registerName("pasteboardWithName:"), name);
    }
}

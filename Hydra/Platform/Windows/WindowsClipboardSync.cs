using System.Drawing;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Text;
using Microsoft.Extensions.Logging;

namespace Hydra.Platform.Windows;

[SupportedOSPlatform("windows")]
public sealed class WindowsClipboardSync : IClipboardSync, IDisposable
{
    // registered once per process; Windows caches the value
    private static readonly uint CfPng = NativeMethods.RegisterClipboardFormat("PNG");
    private static readonly uint CfHtmlFormat = NativeMethods.RegisterClipboardFormat("HTML Format");
    private static readonly uint CfRtf = NativeMethods.RegisterClipboardFormat("Rich Text Format");
    // how password managers mark a copy no clipboard monitor or history should keep
    private static readonly uint CfExcludeFromMonitor = NativeMethods.RegisterClipboardFormat("ExcludeClipboardContentFromMonitorProcessing");
    private static readonly uint CfCanIncludeInHistory = NativeMethods.RegisterClipboardFormat("CanIncludeInClipboardHistory");

    // what GetImagePng reads as an image, in the order it tries them: the "PNG" registered format first (Chrome, Firefox,
    // etc.), then CF_DIB; and the formats whose text is no text copy of the user's
    private static readonly uint[] ImageFormats = [CfPng, NativeMethods.CF_DIB];
    private static readonly uint[] NotTextCopyFormats = [.. ImageFormats, NativeMethods.CF_HDROP];

    private readonly ILogger<WindowsClipboardSync> _log;
    private readonly ClipboardEchoFilter _echo;
    private readonly CopiedPrimary _primary;

    // dedicated owner window for the clipboard — passing NULL to OpenClipboard makes EmptyClipboard
    // set a NULL owner, which per MSDN causes SetClipboardData to fail. A real HWND fixes that.
    private readonly Win32Window? _owner;

    public WindowsClipboardSync(ILogger<WindowsClipboardSync> log)
    {
        _log = log;
        _owner = CreateOwnerWindow();
        _primary = new CopiedPrimary(() => NativeMethods.GetClipboardSequenceNumber(), ReadTextCopy, log);
        _echo = new ClipboardEchoFilter(_primary.Owns);
    }

    // message-only window created once on the constructing (main) thread. No message pump is needed:
    // OpenClipboard/SetClipboardData do not require one — only clipboard-listener notifications do.
    private Win32Window? CreateOwnerWindow()
    {
        try
        {
            return Win32Window.MessageOnly("HydraClipboardOwner");
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "Failed to create clipboard owner window — falling back to NULL owner");
            return null;
        }
    }

    private nint OwnerWindow => _owner?.Handle ?? nint.Zero;

    public void Dispose() => _owner?.Dispose();

    public string? GetText() => WithClipboard(() => _echo.FilterText(ReadOpenText()));

    private static string? ReadOpenText()
    {
        var hMem = NativeMethods.GetClipboardData(NativeMethods.CF_UNICODETEXT);
        if (hMem == nint.Zero) return null;

        var ptr = NativeMethods.GlobalLock(hMem);
        if (ptr == nint.Zero) return null;
        try
        {
            return Marshal.PtrToStringUni(ptr);
        }
        finally
        {
            NativeMethods.GlobalUnlock(hMem);
        }
    }

    // CopiedPrimary's readTextCopy, by clipboard format; one attempt, since it runs under CopiedPrimary's gate
    private string? ReadTextCopy() =>
        NativeMethods.OpenClipboard(OwnerWindow)
            ? WhileOpen(() => NotTextCopyFormats.Any(NativeMethods.IsClipboardFormatAvailable) || IsConcealed() ? null : ReadOpenText())
            : throw new IOException("the clipboard is held by another app");

    private static bool IsConcealed()
    {
        if (NativeMethods.IsClipboardFormatAvailable(CfExcludeFromMonitor)) return true;
        if (!NativeMethods.IsClipboardFormatAvailable(CfCanIncludeInHistory)) return false;
        var allowed = ReadGlobalMemory(NativeMethods.GetClipboardData(CfCanIncludeInHistory));
        return allowed is { Length: >= 4 } && BitConverter.ToUInt32(allowed) == 0;
    }

    public string? GetPrimaryText() => _primary.Read();

    public bool SetPrimaryText(string text) => _primary.Relay(text);

    public bool HasFileClipboard() => NativeMethods.IsClipboardFormatAvailable(NativeMethods.CF_HDROP);

    public bool CanUseEchoFallback() => _primary.Owns();

    public byte[]? GetImagePng() => WithClipboard(() =>
    {
        foreach (var format in ImageFormats)
        {
            if (!NativeMethods.IsClipboardFormatAvailable(format)) continue;
            var data = ReadGlobalMemory(NativeMethods.GetClipboardData(format));
            if (data == null) continue;
            // raw PNG bytes, else a device-independent bitmap converted to PNG
            if (format != CfPng) return DibToPng(data);
            return _echo.FilterImage(data);
        }
        return null;
    });

    public string? GetHtml() => WithClipboard(() =>
    {
        if (!NativeMethods.IsClipboardFormatAvailable(CfHtmlFormat)) return null;
        var blob = ReadGlobalMemory(NativeMethods.GetClipboardData(CfHtmlFormat));
        return _echo.FilterHtml(blob != null ? CfHtml.Unwrap(blob) : null); // strip the Windows CF_HTML wrapper → portable HTML
    });

    // RTF is null-terminated ASCII and GlobalSize may over-report; trim trailing padding/null bytes
    public byte[]? GetRtf() => WithClipboard(() =>
        NativeMethods.IsClipboardFormatAvailable(CfRtf) ? _echo.FilterRtf(TrimTrailingNulls(ReadGlobalMemory(NativeMethods.GetClipboardData(CfRtf)))) : null);

    private T? WithClipboard<T>(Func<T?> read) where T : class => OpenClipboard() ? WhileOpen(read) : null;

    // runs against a clipboard the caller opened, closing it after, then afterClose
    private static T WhileOpen<T>(Func<T> body, Action? afterClose = null)
    {
        try
        {
            return body();
        }
        finally
        {
            NativeMethods.CloseClipboard();
            afterClose?.Invoke();
        }
    }

    public bool SetClipboard(ClipboardSnapshot contents)
    {
        return _primary.Write(contents.PrimaryText, leftAt => OpenClipboard() && WhileOpen(() => WriteOpenClipboard(contents), () =>
        {
            // after closing, which moves the sequence number too; read before asking who owns the clipboard, so
            // another app writing after either leaves it untaken, never taken as ours
            var after = NativeMethods.GetClipboardSequenceNumber();
            if (NativeMethods.GetClipboardOwner() == OwnerWindow) leftAt(after);
        }));
    }

    private bool WriteOpenClipboard(ClipboardSnapshot contents)
    {
        if (!NativeMethods.EmptyClipboard()) return false;
        _echo.Track(contents);

        // image first (legacy image pasters pick the first supported format); rich before plain text
        // so rich-aware apps prefer HTML/RTF over CF_UNICODETEXT.
        ClipboardUtils.WriteAll(_log,
            contents.ImagePng is { } png ? () => WriteImageToOpenClipboard(png) : null,
            contents.Html is { } html ? () => WriteGlobalMemory(CfHtmlFormat, NullTerminated(CfHtml.Wrap(html))) : null,
            contents.Rtf is { } rtf ? () => WriteGlobalMemory(CfRtf, NullTerminated(rtf)) : null,
            contents.Text is { } text ? () => WriteGlobalMemory(NativeMethods.CF_UNICODETEXT, Encoding.Unicode.GetBytes(text + '\0')) : null);
        return true; // emptied, so replaced
    }

    private static byte[] NullTerminated(byte[] data) => [.. data, 0]; // text-based clipboard formats expect a NUL

    private static byte[]? TrimTrailingNulls(byte[]? data)
    {
        if (data == null) return null;
        var len = data.Length;
        while (len > 0 && data[len - 1] == 0) len--;
        return len == data.Length ? data : data[..len];
    }

    private bool WriteImageToOpenClipboard(byte[] pngData)
    {
        // write as "PNG" registered format (raw bytes — modern apps prefer this, full fidelity + alpha)
        var png = WriteGlobalMemory(CfPng, pngData);

        // also write CF_DIBV5 (32bpp, preserves alpha). Windows auto-synthesizes CF_DIB and CF_BITMAP from
        // it for legacy apps (Paint/Office), so a single write covers both alpha-aware and legacy pasters.
        var dib = PngToDibV5(pngData);
        if (dib != null)
            WriteGlobalMemory(NativeMethods.CF_DIBV5, dib);
        return png;
    }

    private static byte[]? ReadGlobalMemory(nint hMem)
    {
        if (hMem == nint.Zero) return null;
        var size = (int)NativeMethods.GlobalSize(hMem);
        if (size <= 0) return null;

        var ptr = NativeMethods.GlobalLock(hMem);
        if (ptr == nint.Zero) return null;
        try
        {
            var bytes = new byte[size];
            Marshal.Copy(ptr, bytes, 0, size);
            return bytes;
        }
        finally
        {
            NativeMethods.GlobalUnlock(hMem);
        }
    }

    // whether the clipboard took it
    private static bool WriteGlobalMemory(uint format, byte[] data)
    {
        var hMem = NativeMethods.GlobalAlloc(NativeMethods.GMEM_MOVEABLE | NativeMethods.GMEM_DDESHARE, (nuint)data.Length);
        if (hMem == nint.Zero) return false;

        var ptr = NativeMethods.GlobalLock(hMem);
        if (ptr == nint.Zero)
        {
            NativeMethods.GlobalFree(hMem);
            return false;
        }

        Marshal.Copy(data, 0, ptr, data.Length);
        NativeMethods.GlobalUnlock(hMem);

        if (NativeMethods.SetClipboardData(format, hMem) != nint.Zero) return true;
        NativeMethods.GlobalFree(hMem);
        return false;
    }

    // converts a PNG to a CF_DIBV5 blob (BITMAPV5HEADER + 32bpp BGRA, bottom-up) preserving alpha.
    private byte[]? PngToDibV5(byte[] pngData)
    {
        try
        {
            using var ms = new MemoryStream(pngData);
            using var bitmap = new Bitmap(ms);

            var width = bitmap.Width;
            var height = bitmap.Height;
            const int headerSize = 124; // BITMAPV5HEADER
            var stride = width * 4;     // 32bpp rows are inherently 4-byte aligned
            var imageSize = stride * height;
            var dib = new byte[headerSize + imageSize];

            BitConverter.GetBytes(headerSize).CopyTo(dib, 0);                  // bV5Size
            BitConverter.GetBytes(width).CopyTo(dib, 4);                       // bV5Width
            BitConverter.GetBytes(height).CopyTo(dib, 8);                      // bV5Height (positive = bottom-up)
            BitConverter.GetBytes((short)1).CopyTo(dib, 12);                   // bV5Planes
            BitConverter.GetBytes((short)32).CopyTo(dib, 14);                  // bV5BitCount
            BitConverter.GetBytes(3).CopyTo(dib, 16);                         // bV5Compression = BI_BITFIELDS
            BitConverter.GetBytes(imageSize).CopyTo(dib, 20);                  // bV5SizeImage
            BitConverter.GetBytes(0x00FF0000).CopyTo(dib, 40);                // bV5RedMask
            BitConverter.GetBytes(0x0000FF00).CopyTo(dib, 44);                // bV5GreenMask
            BitConverter.GetBytes(0x000000FF).CopyTo(dib, 48);                // bV5BlueMask
            BitConverter.GetBytes(unchecked((int)0xFF000000)).CopyTo(dib, 52); // bV5AlphaMask
            BitConverter.GetBytes(0x73524742).CopyTo(dib, 56);                // bV5CSType = LCS_sRGB

            var rect = new Rectangle(0, 0, width, height);
            var bmpData = bitmap.LockBits(rect, ImageLockMode.ReadOnly, PixelFormat.Format32bppArgb);
            try
            {
                // Format32bppArgb is B,G,R,A in memory — matches the BGRA masks above.
                // LockBits gives top-down rows; DIB needs bottom-up — copy in reverse.
                for (var y = 0; y < height; y++)
                {
                    var srcOffset = y * bmpData.Stride;
                    var dstOffset = headerSize + (height - 1 - y) * stride;
                    Marshal.Copy(bmpData.Scan0 + srcOffset, dib, dstOffset, stride);
                }
            }
            finally
            {
                bitmap.UnlockBits(bmpData);
            }

            return dib;
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "PNG to DIBv5 conversion failed");
            return null;
        }
    }

    private byte[]? DibToPng(byte[] dib)
    {
        try
        {
            if (dib.Length < 4) return null;
            // read biSize from the DIB info header (supports extended headers like BITMAPV4/V5)
            var biSize = BitConverter.ToInt32(dib, 0);
            if (biSize < 40 || dib.Length < biSize) return null;

            // compute colour table size for indexed-colour bitmaps
            var colourTableSize = 0;
            var biBitCount = BitConverter.ToUInt16(dib, 14); // offset 14 in BITMAPINFOHEADER
            var biClrUsed = BitConverter.ToInt32(dib, 32);
            if (biBitCount <= 8)
                colourTableSize = (biClrUsed > 0 ? biClrUsed : (1 << biBitCount)) * 4;
            else if (biClrUsed > 0)
                colourTableSize = biClrUsed * 4;

            // a 40-byte BITMAPINFOHEADER with BI_BITFIELDS is followed by 3 (or 4 with alpha) DWORD colour
            // masks that sit BEFORE the pixel data — screenshots (Snipping Tool/PrtScn) arrive this way.
            // (V4/V5 headers carry the masks inside the header, so biSize>40 needs no extra.)
            const int biBitfields = 3, biAlphaBitfields = 6;
            var biCompression = BitConverter.ToInt32(dib, 16);
            var maskSize = biSize == 40 && biCompression == biBitfields ? 12
                : biSize == 40 && biCompression == biAlphaBitfields ? 16
                : 0;

            // prepend 14-byte BMP file header to make a complete BMP file
            var bmpHeader = new byte[14];
            bmpHeader[0] = (byte)'B';
            bmpHeader[1] = (byte)'M';
            var totalSize = dib.Length + 14;
            bmpHeader[2] = (byte)totalSize;
            bmpHeader[3] = (byte)(totalSize >> 8);
            bmpHeader[4] = (byte)(totalSize >> 16);
            bmpHeader[5] = (byte)(totalSize >> 24);
            // pixel data offset: file header + info header + colour table (or BITFIELDS masks)
            var pixelOffset = 14 + biSize + colourTableSize + maskSize;
            bmpHeader[10] = (byte)pixelOffset;
            bmpHeader[11] = (byte)(pixelOffset >> 8);
            bmpHeader[12] = (byte)(pixelOffset >> 16);
            bmpHeader[13] = (byte)(pixelOffset >> 24);

            var bmpBytes = new byte[totalSize];
            bmpHeader.CopyTo(bmpBytes, 0);
            dib.CopyTo(bmpBytes, 14);

            using var ms = new MemoryStream(bmpBytes);
            using var bitmap = new Bitmap(ms);
            using var pngMs = new MemoryStream();
            bitmap.Save(pngMs, ImageFormat.Png);
            return pngMs.ToArray();
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "DIB to PNG conversion failed");
            return null;
        }
    }

    private bool OpenClipboard()
    {
        // clipboard is a global mutex; retry a few times if another app has it
        for (var i = 0; i < 5; i++)
        {
            if (NativeMethods.OpenClipboard(OwnerWindow)) return true;
            Thread.Sleep(5);
        }
        _log.LogWarning("Failed to open clipboard after 5 retries");
        return false;
    }
}

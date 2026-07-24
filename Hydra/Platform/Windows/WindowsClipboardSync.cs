using System.Drawing;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using Microsoft.Extensions.Logging;

namespace Hydra.Platform.Windows;

[SupportedOSPlatform("windows")]
public sealed class WindowsClipboardSync(ILogger<WindowsClipboardSync> log) : IClipboardSync
{
    // registered once per process; Windows caches the value
    private static readonly uint CfPng = NativeMethods.RegisterClipboardFormat("PNG");

    private readonly ILogger<WindowsClipboardSync> _log = log;
    private ClipboardEchoFilter _echo;
    private string? _storedPrimaryText;

    public string? GetText()
    {
        if (!OpenClipboard()) return null;
        try
        {
            var hMem = NativeMethods.GetClipboardData(NativeMethods.CF_UNICODETEXT);
            if (hMem == nint.Zero) return null;

            var ptr = NativeMethods.GlobalLock(hMem);
            if (ptr == nint.Zero) return null;
            try
            {
                var text = Marshal.PtrToStringUni(ptr);
                return _echo.FilterText(text);
            }
            finally
            {
                NativeMethods.GlobalUnlock(hMem);
            }
        }
        finally
        {
            NativeMethods.CloseClipboard();
        }
    }

    public void SetText(string text)
    {
        _echo.TrackText(text);

        if (!OpenClipboard()) return;
        try
        {
            NativeMethods.EmptyClipboard();
            WriteTextToOpenClipboard(text);
        }
        finally
        {
            NativeMethods.CloseClipboard();
        }
    }

    public string? GetPrimaryText() => _storedPrimaryText;

    public void SetPrimaryText(string text) => _storedPrimaryText = text;

    public byte[]? GetImagePng()
    {
        if (!OpenClipboard()) return null;
        try
        {
            // try "PNG" registered format first (Chrome, Firefox, etc. — raw PNG bytes)
            if (NativeMethods.IsClipboardFormatAvailable(CfPng))
            {
                var png = ReadGlobalMemory(NativeMethods.GetClipboardData(CfPng));
                if (png != null)
                {
                    if (_echo.IsDuplicateImage(png)) return null;
                    return png;
                }
            }

            // fall back to CF_DIB (device-independent bitmap) → convert to PNG
            if (!NativeMethods.IsClipboardFormatAvailable(NativeMethods.CF_DIB)) return null;
            var dib = ReadGlobalMemory(NativeMethods.GetClipboardData(NativeMethods.CF_DIB));
            if (dib == null) return null;

            return DibToPng(dib);
        }
        finally
        {
            NativeMethods.CloseClipboard();
        }
    }

    public void SetImagePng(byte[] pngData)
    {
        _echo.TrackImage(pngData);

        if (!OpenClipboard()) return;
        try
        {
            NativeMethods.EmptyClipboard();
            WriteImageToOpenClipboard(pngData);
        }
        finally
        {
            NativeMethods.CloseClipboard();
        }
    }

    public void SetClipboard(ClipboardSnapshot contents)
    {
        var text = contents.Text;
        var primaryText = contents.PrimaryText;
        var imagePng = contents.ImagePng;
        if (text == null && primaryText == null && imagePng == null) return;

        if (text != null) _echo.TrackText(text);
        if (primaryText != null) _storedPrimaryText = primaryText;
        if (imagePng != null) _echo.TrackImage(imagePng);

        if (!OpenClipboard()) return;
        try
        {
            NativeMethods.EmptyClipboard();
            // write image before text so image formats appear first in enumeration —
            // legacy apps (Paint, etc.) pick the first format they support
            if (imagePng != null) WriteImageToOpenClipboard(imagePng);
            if (text != null) WriteTextToOpenClipboard(text);
        }
        finally
        {
            NativeMethods.CloseClipboard();
        }
    }

    private static void WriteTextToOpenClipboard(string text)
    {
        // CF_UNICODETEXT requires null-terminated UTF-16; allocate (length + 1) chars
        var byteCount = (nuint)((text.Length + 1) * 2);
        var hMem = NativeMethods.GlobalAlloc(NativeMethods.GMEM_MOVEABLE, byteCount);
        if (hMem == nint.Zero) return;

        var ptr = NativeMethods.GlobalLock(hMem);
        if (ptr == nint.Zero) { NativeMethods.GlobalFree(hMem); return; }

        Marshal.Copy(text.ToCharArray(), 0, ptr, text.Length);
        Marshal.WriteInt16(ptr + text.Length * 2, 0); // null terminator
        NativeMethods.GlobalUnlock(hMem);

        if (NativeMethods.SetClipboardData(NativeMethods.CF_UNICODETEXT, hMem) == nint.Zero)
            NativeMethods.GlobalFree(hMem);
    }

    private void WriteImageToOpenClipboard(byte[] pngData)
    {
        // write as "PNG" registered format (raw bytes — modern apps prefer this, full fidelity + alpha)
        WriteGlobalMemory(CfPng, pngData);

        // also write CF_DIBV5 (32bpp, preserves alpha). Windows auto-synthesizes CF_DIB and CF_BITMAP from
        // it for legacy apps (Paint/Office), so a single write covers both alpha-aware and legacy pasters.
        var dib = PngToDibV5(pngData);
        if (dib != null)
            WriteGlobalMemory(NativeMethods.CF_DIBV5, dib);
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

    private static void WriteGlobalMemory(uint format, byte[] data)
    {
        var hMem = NativeMethods.GlobalAlloc(NativeMethods.GMEM_MOVEABLE | NativeMethods.GMEM_DDESHARE, (nuint)data.Length);
        if (hMem == nint.Zero) return;

        var ptr = NativeMethods.GlobalLock(hMem);
        if (ptr == nint.Zero) { NativeMethods.GlobalFree(hMem); return; }

        Marshal.Copy(data, 0, ptr, data.Length);
        NativeMethods.GlobalUnlock(hMem);

        if (NativeMethods.SetClipboardData(format, hMem) == nint.Zero)
            NativeMethods.GlobalFree(hMem);
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
            // Without counting them, bfOffBits was 12+ bytes short and the image decoded as garbage / failed.
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
            if (NativeMethods.OpenClipboard(nint.Zero)) return true;
            Thread.Sleep(5);
        }
        _log.LogWarning("Failed to open clipboard after 5 retries");
        return false;
    }
}

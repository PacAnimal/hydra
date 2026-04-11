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
    private string? _lastSetText;
    private ulong? _lastSetImageHash;

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
                return text == _lastSetText ? null : text;
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
        _lastSetText = text;

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
                    if (_lastSetImageHash.HasValue && ClipboardUtils.QuickHash(png) == _lastSetImageHash.Value)
                        return null;
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
        _lastSetImageHash = ClipboardUtils.QuickHash(pngData);

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

    public void SetClipboard(string? text, string? primaryText, byte[]? imagePng)
    {
        if (text == null && primaryText == null && imagePng == null) return;

        if (text != null) _lastSetText = text;
        if (imagePng != null) _lastSetImageHash = ClipboardUtils.QuickHash(imagePng);

        if (!OpenClipboard()) return;
        try
        {
            NativeMethods.EmptyClipboard();
            if (text != null) WriteTextToOpenClipboard(text);
            if (imagePng != null) WriteImageToOpenClipboard(imagePng);
        }
        finally
        {
            NativeMethods.CloseClipboard();
        }
    }

    // clipboard must already be open and emptied before calling these
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

    private static void WriteImageToOpenClipboard(byte[] pngData)
    {
        // write as "PNG" registered format (raw bytes — modern apps prefer this)
        WriteGlobalMemory(CfPng, pngData);

        // also write as CF_BITMAP so legacy apps can paste
        using var ms = new MemoryStream(pngData);
        using var bitmap = new Bitmap(ms);
        var hBitmap = bitmap.GetHbitmap();
        if (hBitmap != nint.Zero)
            NativeMethods.SetClipboardData(NativeMethods.CF_BITMAP, hBitmap);
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
            if (dib.Length >= biSize)
            {
                var biBitCount = BitConverter.ToUInt16(dib, 14); // offset 14 in BITMAPINFOHEADER
                var biClrUsed = BitConverter.ToInt32(dib, 32);
                if (biBitCount <= 8)
                    colourTableSize = (biClrUsed > 0 ? biClrUsed : (1 << biBitCount)) * 4;
                else if (biClrUsed > 0)
                    colourTableSize = biClrUsed * 4;
            }

            // prepend 14-byte BMP file header to make a complete BMP file
            var bmpHeader = new byte[14];
            bmpHeader[0] = (byte)'B';
            bmpHeader[1] = (byte)'M';
            var totalSize = dib.Length + 14;
            bmpHeader[2] = (byte)totalSize;
            bmpHeader[3] = (byte)(totalSize >> 8);
            bmpHeader[4] = (byte)(totalSize >> 16);
            bmpHeader[5] = (byte)(totalSize >> 24);
            // pixel data offset: file header + info header + colour table
            var pixelOffset = 14 + biSize + colourTableSize;
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

    private static bool OpenClipboard()
    {
        // clipboard is a global mutex; retry a few times if another app has it
        for (var i = 0; i < 5; i++)
        {
            if (NativeMethods.OpenClipboard(nint.Zero)) return true;
            Thread.Sleep(5);
        }
        return false;
    }
}

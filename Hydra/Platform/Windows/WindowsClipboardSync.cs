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
    private static readonly uint CfPreferredDropEffect = NativeMethods.RegisterClipboardFormat("Preferred DropEffect");

    public bool SupportsFiles => true;

    private readonly ILogger<WindowsClipboardSync> _log = log;
    private string? _lastSetText;
    private ulong? _lastSetImageHash;
    private HashSet<string>? _lastSetFilePaths;
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

    public void SetClipboard(string? text, string? primaryText, byte[]? imagePng, List<TempFileEntry>? files = null)
    {
        if (text == null && primaryText == null && imagePng == null && files == null) return;

        if (text != null) _lastSetText = text;
        if (primaryText != null) _storedPrimaryText = primaryText;
        if (imagePng != null) _lastSetImageHash = ClipboardUtils.QuickHash(imagePng);
        if (files != null) _lastSetFilePaths = files.ToPathSet();

        if (!OpenClipboard()) return;
        try
        {
            NativeMethods.EmptyClipboard();
            // write image before text so image formats appear first in enumeration —
            // legacy apps (Paint, etc.) pick the first format they support
            if (imagePng != null) WriteImageToOpenClipboard(imagePng);
            if (text != null) WriteTextToOpenClipboard(text);
            if (files != null) WriteFilesToOpenClipboard(files.ToPaths());
        }
        finally
        {
            NativeMethods.CloseClipboard();
        }
    }

    public List<string>? GetFilePaths()
    {
        // try Win32 first. if IsClipboardFormatAvailable returns true but GetClipboardData returns
        // null, Explorer registered CF_HDROP as delayed render and our SYSTEM token can't trigger
        // WM_RENDERFORMAT on its OLE proxy — fall through to OLE in that case too.
        var paths = TryReadFilePathsWin32() ?? ReadFilePathsOle();
        if (paths == null || paths.Count == 0) return null;

        // echo suppression: return null if these are the same paths we just set
        if (_lastSetFilePaths != null && paths.Count == _lastSetFilePaths.Count && paths.All(p => _lastSetFilePaths.Contains(p)))
            return null;

        _log.LogDebug("GetFilePaths: {Count} path(s) from clipboard", paths.Count);
        return paths;
    }

    // returns null if CF_HDROP is not on the Win32 clipboard or GetClipboardData fails
    private List<string>? TryReadFilePathsWin32()
    {
        if (!NativeMethods.IsClipboardFormatAvailable(NativeMethods.CF_HDROP)) return null;
        using var userToken = AcquireSessionUserToken();
        _log.LogDebug("TryReadFilePathsWin32: CF_HDROP available, userToken={HasToken}", userToken != null);
        if (userToken != null) NativeMethods.ImpersonateLoggedOnUser(userToken);
        try
        {
            if (!OpenClipboard()) return null;
            try
            {
                var hDrop = NativeMethods.GetClipboardData(NativeMethods.CF_HDROP);
                _log.LogDebug("TryReadFilePathsWin32: GetClipboardData hDrop={Drop}", hDrop);
                return hDrop == nint.Zero ? null : ExtractPathsFromHDrop(hDrop);
            }
            finally
            {
                NativeMethods.CloseClipboard();
            }
        }
        finally
        {
            if (userToken != null) NativeMethods.RevertToSelf();
        }
    }

    private List<string>? ReadFilePathsOle()
    {
        List<string>? result = null;
        using var userToken = AcquireSessionUserToken();
        _log.LogDebug("ReadFilePathsOle: userToken={HasToken}", userToken != null);
        var t = new Thread(() =>
        {
            // impersonate the interactive user so Explorer's OLE proxy allows the data object request
            if (userToken != null) NativeMethods.ImpersonateLoggedOnUser(userToken);
            // ReSharper disable once MustUseReturnValue
            _ = NativeMethods.OleInitialize(nint.Zero);
            try { result = ReadFilePathsFromDataObject(); }
            finally
            {
                NativeMethods.OleUninitialize();
                if (userToken != null) NativeMethods.RevertToSelf();
            }
        })
        { IsBackground = true, Name = "HydraClipboardSta" };
        t.SetApartmentState(ApartmentState.STA);
        t.Start();
        t.Join();
        return result;
    }

    // in session child mode, returns the interactive user's token so clipboard calls can be impersonated.
    // the session child runs as SYSTEM (winlogon token) — Explorer's delayed-render CF_HDROP and OLE
    // data object both require a user-security-context call to succeed.
    private static Microsoft.Win32.SafeHandles.SafeAccessTokenHandle? AcquireSessionUserToken()
    {
        if (!RunMode.IsSessionChild) return null;
        var session = NativeMethods.GetActiveConsoleSessionId();
        if (NativeMethods.WTSQueryUserToken(session, out var token) && !token.IsInvalid)
            return token;
        token.Dispose();
        return null;
    }

    private List<string>? ReadFilePathsFromDataObject()
    {
        try
        {
            var hr = NativeMethods.OleGetClipboard(out var dataObj);
            _log.LogDebug("OleGetClipboard hr={Hr:X}", hr);
            if (hr != 0) return null;

            // CoInitializeSecurity is often RPC_E_TOO_LATE by the time we call it, so set
            // dynamic cloaking directly on this proxy — COM then uses the thread's impersonated
            // user token instead of the process (SYSTEM) token when calling into Explorer
            if (RunMode.IsSessionChild)
            {
                var pProxy = Marshal.GetComInterfaceForObject(dataObj,
                    typeof(System.Runtime.InteropServices.ComTypes.IDataObject));
                try
                {
                    var blanketHr = NativeMethods.CoSetProxyBlanket(pProxy,
                        0xFFFFFFFF, 0xFFFFFFFF, nint.Zero,  // RPC_C_AUTHN_DEFAULT, RPC_C_AUTHZ_DEFAULT, COLE_DEFAULT_PRINCIPAL
                        0xFFFFFFFF, 3,                       // RPC_C_AUTHN_LEVEL_DEFAULT, RPC_C_IMP_LEVEL_IMPERSONATE
                        nint.Zero, 0x40);                    // pAuthInfo=null (use thread token), EOAC_DYNAMIC_CLOAKING
                    _log.LogDebug("CoSetProxyBlanket hr={Hr:X}", blanketHr);
                }
                finally { Marshal.Release(pProxy); }
            }

            // log all available formats so we can see what Explorer actually exposes
            var enumFmt = dataObj.EnumFormatEtc(System.Runtime.InteropServices.ComTypes.DATADIR.DATADIR_GET);
            if (enumFmt != null)
            {
                enumFmt.Reset();
                var buf = new System.Runtime.InteropServices.ComTypes.FORMATETC[1];
                var fetched = new int[1];
                while (enumFmt.Next(1, buf, fetched) == 0)
                    _log.LogDebug("OLE available format: cfFormat={Fmt} tymed={Tymed}", (ushort)buf[0].cfFormat, buf[0].tymed);
            }

            var fmt = new System.Runtime.InteropServices.ComTypes.FORMATETC
            {
                cfFormat = (short)NativeMethods.CF_HDROP,
                ptd = nint.Zero,
                dwAspect = System.Runtime.InteropServices.ComTypes.DVASPECT.DVASPECT_CONTENT,
                lindex = -1,
                tymed = System.Runtime.InteropServices.ComTypes.TYMED.TYMED_HGLOBAL,
            };

            System.Runtime.InteropServices.ComTypes.STGMEDIUM medium;
            try { dataObj.GetData(ref fmt, out medium); }
            catch (Exception ex)
            {
                _log.LogDebug(ex, "OLE GetData(CF_HDROP) failed — no files on clipboard");
                return null;
            }

            if (medium.tymed != System.Runtime.InteropServices.ComTypes.TYMED.TYMED_HGLOBAL || medium.unionmember == nint.Zero)
            {
                NativeMethods.ReleaseStgMedium(ref medium);
                return null;
            }

            try { return ExtractPathsFromHDrop(medium.unionmember); }
            finally { NativeMethods.ReleaseStgMedium(ref medium); }
        }
        catch (Exception ex)
        {
            _log.LogDebug(ex, "OLE clipboard read failed");
            return null;
        }
    }

    private static List<string>? ExtractPathsFromHDrop(nint hDrop)
    {
        uint count;
        unsafe { count = NativeMethods.DragQueryFileW(hDrop, 0xFFFFFFFF, null, 0); }
        if (count == 0) return null;

        var paths = new List<string>((int)count);
        for (uint i = 0; i < count; i++)
        {
            // query required length first (returns char count, excluding null terminator)
            uint needed;
            unsafe { needed = NativeMethods.DragQueryFileW(hDrop, i, null, 0); }
            if (needed == 0) continue;
            var buf = new char[needed + 1];
            unsafe
            {
                fixed (char* p = buf)
                {
                    var len = NativeMethods.DragQueryFileW(hDrop, i, p, needed + 1);
                    if (len > 0) paths.Add(new string(p, 0, (int)len));
                }
            }
        }

        return paths.Count > 0 ? paths : null;
    }

    // clipboard must already be open and emptied before calling these
    private void WriteFilesToOpenClipboard(List<string> paths)
    {
        // DROPFILES struct layout (total 20 bytes):
        //   DWORD pFiles  @ 0  — byte offset of file list from start of struct
        //   POINT pt      @ 4  — drop point (unused, set to 0,0)
        //   BOOL  fNC     @ 12 — non-client area flag (unused)
        //   BOOL  fWide   @ 16 — 1 = UTF-16 paths
        const int offPFiles = 0;
        const int offPt = 4;
        const int offFnc = 12;
        const int offFWide = 16;
        const int headerSize = 20;

        // file list: each path as null-terminated UTF-16, then a double-null terminator
        using var pathBlob = new MemoryStream();
        foreach (var path in paths)
            pathBlob.Write(System.Text.Encoding.Unicode.GetBytes(path + "\0"));
        pathBlob.Write(new byte[2]); // double-null terminator

        var data = pathBlob.ToArray();
        var totalSize = headerSize + data.Length;

        var hMem = NativeMethods.GlobalAlloc(NativeMethods.GMEM_MOVEABLE | NativeMethods.GMEM_DDESHARE, (nuint)totalSize);
        if (hMem == nint.Zero) return;

        var ptr = NativeMethods.GlobalLock(hMem);
        if (ptr == nint.Zero) { NativeMethods.GlobalFree(hMem); return; }

        try
        {
            Marshal.WriteInt32(ptr, offPFiles, headerSize); // pFiles: file list starts right after header
            Marshal.WriteInt32(ptr, offPt, 0);              // pt.x
            Marshal.WriteInt32(ptr, offPt + 4, 0);          // pt.y
            Marshal.WriteInt32(ptr, offFnc, 0);             // fNC
            Marshal.WriteInt32(ptr, offFWide, 1);           // fWide: Unicode paths
            Marshal.Copy(data, 0, ptr + headerSize, data.Length);
        }
        finally
        {
            NativeMethods.GlobalUnlock(hMem);
        }

        if (NativeMethods.SetClipboardData(NativeMethods.CF_HDROP, hMem) == nint.Zero)
        {
            _log.LogWarning("SetClipboardData(CF_HDROP) failed for {Count} path(s) (error {Error})", paths.Count, Marshal.GetLastWin32Error());
            NativeMethods.GlobalFree(hMem);
            return;
        }
        _log.LogDebug("WriteFilesToOpenClipboard: set {Count} path(s)", paths.Count);

        // Preferred DropEffect tells Explorer this is a Copy, not a Move
        var hEffect = NativeMethods.GlobalAlloc(NativeMethods.GMEM_MOVEABLE | NativeMethods.GMEM_DDESHARE, 4);
        if (hEffect != nint.Zero)
        {
            var pEffect = NativeMethods.GlobalLock(hEffect);
            if (pEffect != nint.Zero)
            {
                Marshal.WriteInt32(pEffect, 1); // DROPEFFECT_COPY
                NativeMethods.GlobalUnlock(hEffect);
                if (NativeMethods.SetClipboardData(CfPreferredDropEffect, hEffect) == nint.Zero)
                    NativeMethods.GlobalFree(hEffect);
            }
            else
            {
                NativeMethods.GlobalFree(hEffect);
            }
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
        // write as "PNG" registered format (raw bytes — modern apps prefer this)
        WriteGlobalMemory(CfPng, pngData);

        // also write as CF_DIB so legacy apps (Paint, etc.) can paste.
        // windows auto-synthesizes CF_BITMAP from CF_DIB on demand — the reliable direction.
        var dib = PngToDib(pngData);
        if (dib != null)
            WriteGlobalMemory(NativeMethods.CF_DIB, dib);
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

    private byte[]? PngToDib(byte[] pngData)
    {
        try
        {
            using var ms = new MemoryStream(pngData);
            using var bitmap = new Bitmap(ms);

            var width = bitmap.Width;
            var height = bitmap.Height;
            const int bpp = 24;
            const int headerSize = 40; // BITMAPINFOHEADER

            // DIB rows are padded to 4-byte boundary
            var dibStride = ((width * bpp + 31) & ~31) >> 3;
            var imageSize = dibStride * height;
            var dib = new byte[headerSize + imageSize];

            // BITMAPINFOHEADER (40 bytes, remaining fields are 0 = BI_RGB)
            BitConverter.GetBytes(headerSize).CopyTo(dib, 0);  // biSize
            BitConverter.GetBytes(width).CopyTo(dib, 4);       // biWidth
            BitConverter.GetBytes(height).CopyTo(dib, 8);      // biHeight (positive = bottom-up)
            BitConverter.GetBytes((short)1).CopyTo(dib, 12);   // biPlanes
            BitConverter.GetBytes((short)bpp).CopyTo(dib, 14); // biBitCount
            BitConverter.GetBytes(imageSize).CopyTo(dib, 20);  // biSizeImage

            var rect = new Rectangle(0, 0, width, height);
            var bmpData = bitmap.LockBits(rect, ImageLockMode.ReadOnly, PixelFormat.Format24bppRgb);
            try
            {
                var srcStride = Math.Abs(bmpData.Stride);
                // LockBits gives top-down rows; DIB needs bottom-up — copy in reverse
                for (var y = 0; y < height; y++)
                {
                    var srcOffset = y * bmpData.Stride;
                    var dstOffset = headerSize + (height - 1 - y) * dibStride;
                    Marshal.Copy(bmpData.Scan0 + srcOffset, dib, dstOffset, Math.Min(srcStride, dibStride));
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
            _log.LogWarning(ex, "PNG to DIB conversion failed");
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

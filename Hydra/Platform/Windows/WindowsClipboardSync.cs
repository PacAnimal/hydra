using System.Runtime.InteropServices;

namespace Hydra.Platform.Windows;

public sealed class WindowsClipboardSync : IClipboardSync
{
    private string? _lastSetText;

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
                NativeMethods.GlobalFree(hMem); // clipboard didn't take ownership
        }
        finally
        {
            NativeMethods.CloseClipboard();
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

namespace Hydra.Platform.Windows;

// saves the current system cursors, replaces them with a blank, and restores them on demand.
// (SPI_SETCURSORS reloads from HKCU which maps to the wrong user when running under a winlogon token)
internal sealed class WindowsCursorSnapshot : IDisposable
{
    private nint[]? _saved;
    public bool IsHidden { get; private set; }

    public unsafe void Hide()
    {
        if (IsHidden) return;

        var ids = NativeMethods.AllCursorIds;
        _saved = new nint[ids.Length];
        for (var i = 0; i < ids.Length; i++)
        {
            var shared = NativeMethods.LoadCursor(nint.Zero, (nint)ids[i]);
            _saved[i] = shared != nint.Zero ? NativeMethods.CopyCursor(shared) : nint.Zero;
        }

        byte andMask = 0xFF;
        byte xorMask = 0x00;
        var blank = NativeMethods.CreateCursor(nint.Zero, 0, 0, 1, 1, &andMask, &xorMask);
        if (blank == nint.Zero) return;
        foreach (var id in ids)
        {
            var copy = NativeMethods.CopyCursor(blank);
            if (copy != nint.Zero)
                NativeMethods.SetSystemCursor(copy, id);
        }
        NativeMethods.DestroyCursor(blank);
        IsHidden = true;
    }

    public void Show()
    {
        if (!IsHidden) return;
        Restore();
        IsHidden = false;
    }

    private void Restore()
    {
        if (_saved == null) return;
        var ids = NativeMethods.AllCursorIds;
        for (var i = 0; i < ids.Length; i++)
        {
            if (_saved[i] == nint.Zero) continue;
            // SetSystemCursor takes ownership; pass a fresh copy so we keep our saved handle
            var copy = NativeMethods.CopyCursor(_saved[i]);
            if (copy != nint.Zero)
                NativeMethods.SetSystemCursor(copy, ids[i]);
        }
    }

    public void Dispose()
    {
        if (IsHidden) Restore();
        if (_saved != null)
        {
            foreach (var h in _saved)
                if (h != nint.Zero) NativeMethods.DestroyCursor(h);
            _saved = null;
        }
    }
}

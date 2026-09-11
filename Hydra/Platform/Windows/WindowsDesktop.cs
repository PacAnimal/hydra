namespace Hydra.Platform.Windows;

// Desktops are identified by NAME, never by handle: OpenInputDesktop hands back a fresh handle on every
// call, so two handles onto the same desktop never compare equal.
internal static class WindowsDesktop
{
    internal static unsafe string Name(nint hDesk)
    {
        if (hDesk == nint.Zero) return "";
        const int bufSize = 128;
        char* buf = stackalloc char[bufSize];
        return NativeMethods.GetUserObjectInformationW(hDesk, NativeMethods.UOI_NAME, (nint)buf, bufSize * sizeof(char), out _)
            ? new string(buf)
            : "";
    }

    // name of the desktop the user's keyboard and mouse are actually going to, or "" if it cannot be read
    internal static string InputDesktopName()
    {
        var hDesk = NativeMethods.OpenInputDesktop(0, false, NativeMethods.DESKTOP_READOBJECTS);
        if (hDesk == nint.Zero) return "";
        try
        {
            return Name(hDesk);
        }
        finally
        {
            NativeMethods.CloseDesktop(hDesk);
        }
    }
}

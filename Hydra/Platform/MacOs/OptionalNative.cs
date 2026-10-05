using System.Runtime.InteropServices;

namespace Hydra.Platform.MacOs;

// private frameworks and symbols that may be missing from a given macOS release: absent means zero or null, never a throw
internal static class OptionalNative
{
    internal static nint LoadLibrary(string path)
    {
        try { return NativeLibrary.Load(path); }
        catch { return nint.Zero; }
    }

    internal static T? LoadDelegate<T>(nint handle, string symbol) where T : Delegate
    {
        if (handle == nint.Zero) return null;
        try { return Marshal.GetDelegateForFunctionPointer<T>(NativeLibrary.GetExport(handle, symbol)); }
        catch { return null; }
    }
}

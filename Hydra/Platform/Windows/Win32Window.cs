using System.ComponentModel;
using System.Runtime.InteropServices;

namespace Hydra.Platform.Windows;

// A window and a class of its own, so no window ever runs another instance's WndProc after that one was collected,
// and a second instance never finds the class taken. Made and disposed on the thread that pumps its messages.
internal sealed class Win32Window : IDisposable
{
    private static int _instances;

    private readonly WndProc _wndProc; // the class calls it, so it lives until the class is unregistered
    private readonly nint _hInstance = NativeMethods.GetModuleHandleW(nint.Zero);
    private ushort _atom;

    internal nint Handle { get; private set; }

    // throws Win32Exception if the class or the window cannot be made
    internal Win32Window(string name, WndProc wndProc, uint style = 0, uint exStyle = 0, nint parent = 0, nint background = 0)
    {
        _wndProc = wndProc;
        var className = Marshal.StringToHGlobalUni($"{name}.{Interlocked.Increment(ref _instances)}");
        try
        {
            var wc = new NativeMethods.WNDCLASSEXW
            {
                cbSize = (uint)Marshal.SizeOf<NativeMethods.WNDCLASSEXW>(),
                lpfnWndProc = Marshal.GetFunctionPointerForDelegate(_wndProc),
                hInstance = _hInstance,
                hbrBackground = background,
                lpszClassName = className,
            };
            _atom = NativeMethods.RegisterClassExW(in wc);
            if (_atom == 0) throw new Win32Exception(Marshal.GetLastWin32Error(), $"RegisterClassExW({name}) failed");
        }
        finally
        {
            Marshal.FreeHGlobal(className);
        }

        Handle = NativeMethods.CreateWindowExW(exStyle, _atom, nint.Zero, style, 0, 0, 1, 1, parent, nint.Zero, _hInstance, nint.Zero);
        if (Handle != nint.Zero) return;
        var error = Marshal.GetLastWin32Error();
        Dispose();
        throw new Win32Exception(error, $"CreateWindowExW({name}) failed");
    }

    // a window that only receives messages: no desktop, never shown
    internal static Win32Window MessageOnly(string name, WndProc? wndProc = null) =>
        new(name, wndProc ?? NativeMethods.DefWindowProcW, parent: NativeMethods.HWND_MESSAGE);

    // the class goes after the window: one with a live window cannot be unregistered
    public void Dispose()
    {
        if (Handle != nint.Zero) NativeMethods.DestroyWindow(Handle);
        Handle = nint.Zero;
        if (_atom != 0) NativeMethods.UnregisterClassW(_atom, _hInstance);
        _atom = 0;
        GC.KeepAlive(_wndProc);
    }
}

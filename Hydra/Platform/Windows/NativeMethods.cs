using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace Hydra.Platform.Windows;

internal static partial class NativeMethods
{
    private const string User32 = "user32.dll";
    private const string Kernel32 = "kernel32.dll";

    // -- hook types --

    internal const int WH_MOUSE_LL = 14;
    internal const int WH_KEYBOARD_LL = 13;

    // -- wParam values for WH_MOUSE_LL --

    internal const int WM_MOUSEMOVE = 0x0200;

    // -- wParam values for WH_KEYBOARD_LL --

    internal const int WM_KEYDOWN = 0x0100;
    internal const int WM_KEYUP = 0x0101;
    internal const int WM_SYSKEYDOWN = 0x0104;
    internal const int WM_SYSKEYUP = 0x0105;

    // -- message loop --

    internal const uint WM_QUIT = 0x0012;

    // -- GetSystemMetrics indices --

    internal const int SM_CXSCREEN = 0;
    internal const int SM_CYSCREEN = 1;

    // -- KBDLLHOOKSTRUCT flags --

    internal const uint LLKHF_EXTENDED = 0x01;
    internal const uint LLKHF_INJECTED = 0x10;

    // -- virtual key codes --

    internal const uint VK_SPACE = 0x20;

    // -- hooks --

    [LibraryImport(User32, EntryPoint = "SetWindowsHookExW")]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvStdcall)])]
    internal static partial nint SetWindowsHookExW(int idHook, HookProc lpfn, nint hMod, uint dwThreadId);

    [LibraryImport(User32)]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvStdcall)])]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool UnhookWindowsHookEx(nint hhk);

    [LibraryImport(User32)]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvStdcall)])]
    internal static partial nint CallNextHookEx(nint hhk, int nCode, nint wParam, nint lParam);

    // -- message pump --

    [LibraryImport(User32, EntryPoint = "GetMessageW")]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvStdcall)])]
    internal static partial int GetMessage(out MSG lpMsg, nint hWnd, uint wMsgFilterMin, uint wMsgFilterMax);

    [LibraryImport(User32)]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvStdcall)])]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool TranslateMessage(in MSG lpMsg);

    [LibraryImport(User32, EntryPoint = "DispatchMessageW")]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvStdcall)])]
    internal static partial nint DispatchMessage(in MSG lpMsg);

    [LibraryImport(User32, EntryPoint = "PostThreadMessageW")]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvStdcall)])]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool PostThreadMessage(uint idThread, uint msg, nint wParam, nint lParam);

    // -- cursor --

    [LibraryImport(User32)]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvStdcall)])]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool SetCursorPos(int x, int y);

    [LibraryImport(User32)]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvStdcall)])]
    internal static partial int ShowCursor([MarshalAs(UnmanagedType.Bool)] bool bShow);

    // OCR_NORMAL = the default arrow cursor id for SetSystemCursor
    internal const uint OCR_NORMAL = 32512;

    // SPI_SETCURSORS = restore all system cursors to their defaults
    internal const uint SPI_SETCURSORS = 0x0057;

    [LibraryImport(User32)]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvStdcall)])]
    internal static unsafe partial nint CreateCursor(
        nint hInst, int xHotSpot, int yHotSpot, int nWidth, int nHeight,
        byte* pvANDPlane, byte* pvXORPlane);

    [LibraryImport(User32)]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvStdcall)])]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool SetSystemCursor(nint hcur, uint id);

    [LibraryImport(User32, EntryPoint = "SystemParametersInfoW")]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvStdcall)])]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool SystemParametersInfo(uint uiAction, uint uiParam, nint pvParam, uint fWinIni);

    // -- display --

    [LibraryImport(User32)]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvStdcall)])]
    internal static partial int GetSystemMetrics(int nIndex);

    // -- keyboard --

    [LibraryImport(User32)]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvStdcall)])]
    internal static partial nint GetKeyboardLayout(uint idThread);

    [LibraryImport(User32)]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvStdcall)])]
    internal static unsafe partial int ToUnicodeEx(
        uint wVirtKey, uint wScanCode, byte* lpKeyState,
        char* pwszBuff, int cchBuff, uint wFlags, nint dwhkl);

    // -- kernel --

    [LibraryImport(Kernel32)]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvStdcall)])]
    internal static partial uint GetCurrentThreadId();

    // -- foreground window (for keyboard layout detection) --

    [LibraryImport(User32)]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvStdcall)])]
    internal static partial nint GetForegroundWindow();

    [LibraryImport(User32)]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvStdcall)])]
    internal static partial uint GetWindowThreadProcessId(nint hWnd, out uint lpdwProcessId);

    // low word: toggle state (bit 0); high word: pressed state (bit 15)
    [LibraryImport(User32)]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvStdcall)])]
    internal static partial short GetKeyState(int nVirtKey);
}

[UnmanagedFunctionPointer(CallingConvention.StdCall)]
internal delegate nint HookProc(int nCode, nint wParam, nint lParam);

[StructLayout(LayoutKind.Sequential)]
internal struct MSLLHOOKSTRUCT
{
    internal WINPOINT pt;
    internal uint mouseData;
    internal uint flags;
    internal uint time;
    internal nuint dwExtraInfo;
}

[StructLayout(LayoutKind.Sequential)]
internal struct KBDLLHOOKSTRUCT
{
    internal uint vkCode;
    internal uint scanCode;
    internal uint flags;
    internal uint time;
    internal nuint dwExtraInfo;
}

[StructLayout(LayoutKind.Sequential)]
internal struct WINPOINT
{
    internal int x;
    internal int y;
}

[StructLayout(LayoutKind.Sequential)]
internal struct MSG
{
    internal nint hwnd;
    internal uint message;
    internal nint wParam;
    internal nint lParam;
    internal uint time;
    internal WINPOINT pt;
    internal uint lPrivate;
}

using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
// ReSharper disable InconsistentNaming

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
    internal const int WM_LBUTTONDOWN = 0x0201;
    internal const int WM_LBUTTONUP = 0x0202;
    internal const int WM_RBUTTONDOWN = 0x0204;
    internal const int WM_RBUTTONUP = 0x0205;
    internal const int WM_MBUTTONDOWN = 0x0207;
    internal const int WM_MBUTTONUP = 0x0208;
    internal const int WM_MOUSEWHEEL = 0x020A;
    internal const int WM_XBUTTONDOWN = 0x020B;
    internal const int WM_XBUTTONUP = 0x020C;
    internal const int WM_MOUSEHWHEEL = 0x020E;

    // XBUTTON identifiers (HIWORD of mouseData for WM_XBUTTON*)
    internal const int XBUTTON1 = 1;
    internal const int XBUTTON2 = 2;

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
    internal const int SM_XVIRTUALSCREEN = 76;
    internal const int SM_YVIRTUALSCREEN = 77;
    internal const int SM_CXVIRTUALSCREEN = 78;
    internal const int SM_CYVIRTUALSCREEN = 79;

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
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool GetCursorPos(out WINPOINT lpPoint);

    // OCR_* = standard system cursor ids for SetSystemCursor
    internal const uint OCR_NORMAL = 32512;
    internal const uint OCR_IBEAM = 32513;
    internal const uint OCR_WAIT = 32514;
    internal const uint OCR_CROSS = 32515;
    internal const uint OCR_UP = 32516;
    internal const uint OCR_SIZENWSE = 32642;
    internal const uint OCR_SIZENESW = 32643;
    internal const uint OCR_SIZEWE = 32644;
    internal const uint OCR_SIZENS = 32645;
    internal const uint OCR_SIZEALL = 32646;
    internal const uint OCR_NO = 32648;
    internal const uint OCR_HAND = 32649;
    internal const uint OCR_APPSTARTING = 32650;

    internal static readonly uint[] AllCursorIds =
    [
        OCR_NORMAL, OCR_IBEAM, OCR_WAIT, OCR_CROSS, OCR_UP,
        OCR_SIZENWSE, OCR_SIZENESW, OCR_SIZEWE, OCR_SIZENS,
        OCR_SIZEALL, OCR_NO, OCR_HAND, OCR_APPSTARTING,
    ];

    // SPI_SETCURSORS = restore all system cursors to their defaults
    internal const uint SPI_SETCURSORS = 0x0057;

    // SPI_GETKEYBOARDDELAY: 0-3 → 250/500/750/1000ms initial repeat delay
    internal const uint SPI_GETKEYBOARDDELAY = 0x0016;
    // SPI_GETKEYBOARDSPEED: 0-31 → ~33ms-500ms per-repeat interval (linear; 0=slowest, 31=fastest)
    internal const uint SPI_GETKEYBOARDSPEED = 0x000A;

    // mouse acceleration — save/restore around relative moves to get 1:1 movement (matches input-leap)
    internal const uint SPI_GETMOUSE = 0x0003;
    internal const uint SPI_SETMOUSE = 0x0004;
    internal const uint SPI_GETMOUSESPEED = 0x0070;
    internal const uint SPI_SETMOUSESPEED = 0x0071;

    [LibraryImport(User32)]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvStdcall)])]
    internal static unsafe partial nint CreateCursor(
        nint hInst, int xHotSpot, int yHotSpot, int nWidth, int nHeight,
        byte* pvANDPlane, byte* pvXORPlane);

    [LibraryImport(User32, EntryPoint = "CopyIcon")]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvStdcall)])]
    internal static partial nint CopyCursor(nint hcur);

    [LibraryImport(User32)]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvStdcall)])]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool DestroyCursor(nint hCursor);

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

    // EnumDisplayMonitors: classic DllImport for managed delegate marshaling
    [LibraryImport(User32, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool EnumDisplayMonitors(nint hdc, nint lprcClip, MonitorEnumProc lpfnEnum, nint dwData);

#pragma warning disable SYSLIB1054 // ByValTStr not supported by LibraryImport source generator
    [DllImport(User32, EntryPoint = "GetMonitorInfoW", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool GetMonitorInfoW(nint hMonitor, ref MONITORINFOEX lpmi);
#pragma warning restore SYSLIB1054

    // -- input injection --

    internal const uint INPUT_MOUSE = 0;
    internal const uint INPUT_KEYBOARD = 1;

    internal const uint KEYEVENTF_EXTENDEDKEY = 0x01;
    internal const uint KEYEVENTF_KEYUP = 0x02;
    internal const uint KEYEVENTF_UNICODE = 0x04;

    internal const uint MOUSEEVENTF_MOVE = 0x0001;
    internal const uint MOUSEEVENTF_LEFTDOWN = 0x0002;
    internal const uint MOUSEEVENTF_LEFTUP = 0x0004;
    internal const uint MOUSEEVENTF_RIGHTDOWN = 0x0008;
    internal const uint MOUSEEVENTF_RIGHTUP = 0x0010;
    internal const uint MOUSEEVENTF_MIDDLEDOWN = 0x0020;
    internal const uint MOUSEEVENTF_MIDDLEUP = 0x0040;
    internal const uint MOUSEEVENTF_XDOWN = 0x0080;
    internal const uint MOUSEEVENTF_XUP = 0x0100;
    internal const uint MOUSEEVENTF_WHEEL = 0x0800;
    internal const uint MOUSEEVENTF_HWHEEL = 0x1000;
    internal const uint MOUSEEVENTF_VIRTUALDESK = 0x4000;
    internal const uint MOUSEEVENTF_ABSOLUTE = 0x8000;

    [LibraryImport(User32)]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvStdcall)])]
    internal static unsafe partial uint SendInput(uint nInputs, INPUT* pInputs, int cbSize);

    // -- keyboard --

    [LibraryImport(User32)]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvStdcall)])]
    internal static partial nint GetKeyboardLayout(uint idThread);

    // returns VK in low byte, shift state in high byte; -1 if no mapping
    [LibraryImport(User32, EntryPoint = "VkKeyScanW")]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvStdcall)])]
    internal static partial short VkKeyScanW(ushort ch);

    [LibraryImport(User32)]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvStdcall)])]
    internal static unsafe partial int ToUnicodeEx(
        uint wVirtKey, uint wScanCode, byte* lpKeyState,
        char* pwszBuff, int cchBuff, uint wFlags, nint dwhkl);

    [LibraryImport(User32)]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvStdcall)])]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool LockWorkStation();

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

    // -- screensaver sync --

    // SPI_GETSCREENSAVERRUNNING: pvParam receives BOOL (1 if screensaver is running)
    internal const uint SPI_GETSCREENSAVERRUNNING = 0x0072;
    // SPI_SETSCREENSAVEACTIVE: uiParam = 1 to enable, 0 to disable
    internal const uint SPI_SETSCREENSAVEACTIVE = 0x0011;

    internal const uint WM_SYSCOMMAND = 0x0112;
    internal const nint SC_SCREENSAVE = 0xF140;
    internal const uint WM_CLOSE = 0x0010;

    [LibraryImport(User32)]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvStdcall)])]
    internal static partial nint GetDesktopWindow();

    [LibraryImport(User32)]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvStdcall)])]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool PostMessage(nint hWnd, uint msg, nint wParam, nint lParam);

    // SetThreadExecutionState: prevents sleep/screensaver
    internal const uint ES_DISPLAY_REQUIRED = 0x00000002;

    [LibraryImport(Kernel32)]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvStdcall)])]
    internal static partial uint SetThreadExecutionState(uint esFlags);
}

[UnmanagedFunctionPointer(CallingConvention.StdCall)]
internal delegate bool MonitorEnumProc(nint hMonitor, nint hdcMonitor, ref WINRECT lprcMonitor, nint dwData);

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

[StructLayout(LayoutKind.Sequential)]
internal struct MOUSEINPUT
{
    internal int dx;
    internal int dy;
    internal uint mouseData;
    internal uint dwFlags;
    internal uint time;
    internal nuint dwExtraInfo;
}

[StructLayout(LayoutKind.Sequential)]
internal struct KEYBDINPUT
{
    internal ushort wVk;
    internal ushort wScan;
    internal uint dwFlags;
    internal uint time;
    internal nuint dwExtraInfo;
}

// INPUT is a union of MOUSEINPUT and KEYBDINPUT (plus HARDWAREINPUT, unused).
// the union starts at offset 8 on x64 (4 bytes padding after type for 8-byte alignment of ULONG_PTR)
[StructLayout(LayoutKind.Explicit, Size = 40)]
internal struct INPUT
{
    [FieldOffset(0)] internal uint type;
    [FieldOffset(8)] internal MOUSEINPUT mi;
    [FieldOffset(8)] internal KEYBDINPUT ki;
}

[StructLayout(LayoutKind.Sequential)]
internal struct WINRECT
{
    internal int Left, Top, Right, Bottom;
}

[StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode, Pack = 4)]
internal struct MONITORINFOEX
{
    internal uint Size;
    internal WINRECT Monitor;
    internal WINRECT Work;
    internal uint Flags;
    [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)]
    internal string DeviceName;
}

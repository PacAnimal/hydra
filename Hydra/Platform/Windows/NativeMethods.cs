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
    internal const uint WM_USER = 0x0400;

    // -- system colors (use as hbrBackground: cast to nint, add 1) --
    internal const int COLOR_BTNFACE = 15;

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

    [LibraryImport(User32)]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvStdcall)])]
    internal static partial nint WindowFromPoint(WINPOINT pt);

    internal const uint GA_ROOTOWNER = 3;

    [LibraryImport(User32)]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvStdcall)])]
    internal static partial nint GetAncestor(nint hwnd, uint gaFlags);

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

    [LibraryImport(User32, EntryPoint = "LoadCursorW")]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvStdcall)])]
    internal static partial nint LoadCursor(nint hInstance, nint lpCursorName);

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

    [LibraryImport(User32)]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvStdcall)])]
    internal static partial uint GetDpiForSystem();

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

    // -- window management --

    internal const uint WS_POPUP = 0x80000000;
    internal const uint WS_DISABLED = 0x08000000;
    internal const uint WS_EX_TOPMOST = 0x00000008;
    internal const uint WS_EX_TOOLWINDOW = 0x00000080;
    internal const uint WS_EX_LAYERED = 0x00080000;
    internal const uint WS_EX_NOACTIVATE = 0x08000000;

    internal static readonly nint HWND_TOPMOST = new(-1);
    internal static readonly nint HWND_BOTTOM = new(1);

    internal const uint SWP_NOSIZE = 0x0001;
    internal const uint SWP_NOMOVE = 0x0002;
    internal const uint SWP_NOACTIVATE = 0x0010;
    internal const uint SWP_SHOWWINDOW = 0x0040;
    internal const uint SWP_HIDEWINDOW = 0x0080;

    internal const int SW_HIDE = 0;
    internal const uint LWA_ALPHA = 0x00000002;

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    internal struct WNDCLASSEXW
    {
        internal uint cbSize;
        internal uint style;
        internal nint lpfnWndProc;
        internal int cbClsExtra;
        internal int cbWndExtra;
        internal nint hInstance;
        internal nint hIcon;
        internal nint hCursor;
        internal nint hbrBackground;
        internal nint lpszMenuName;
        internal nint lpszClassName;
        internal nint hIconSm;
    }

    [LibraryImport(User32, EntryPoint = "RegisterClassExW")]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvStdcall)])]
    internal static partial ushort RegisterClassExW(in WNDCLASSEXW lpwcx);

    [LibraryImport(User32, EntryPoint = "CreateWindowExW")]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvStdcall)])]
    internal static partial nint CreateWindowExW(
        uint dwExStyle, nint lpClassName, nint lpWindowName,
        uint dwStyle, int x, int y, int nWidth, int nHeight,
        nint hWndParent, nint hMenu, nint hInstance, nint lpParam);

    [LibraryImport(User32)]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvStdcall)])]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool DestroyWindow(nint hWnd);

    [LibraryImport(User32, EntryPoint = "DefWindowProcW")]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvStdcall)])]
    internal static partial nint DefWindowProcW(nint hWnd, uint msg, nint wParam, nint lParam);

    [LibraryImport(User32)]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvStdcall)])]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool ShowWindow(nint hWnd, int nCmdShow);

    [LibraryImport(User32)]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvStdcall)])]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool SetWindowPos(nint hWnd, nint hWndInsertAfter, int x, int y, int cx, int cy, uint uFlags);

    [LibraryImport(User32)]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvStdcall)])]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool EnableWindow(nint hWnd, [MarshalAs(UnmanagedType.Bool)] bool bEnable);

    [LibraryImport(User32)]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvStdcall)])]
    internal static partial nint SetActiveWindow(nint hWnd);

    [LibraryImport(User32)]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvStdcall)])]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool SetForegroundWindow(nint hWnd);

    [LibraryImport(User32)]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvStdcall)])]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool SetLayeredWindowAttributes(nint hwnd, uint crKey, byte bAlpha, uint dwFlags);

    [LibraryImport(User32)]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvStdcall)])]
    internal static partial nint SetCursor(nint hCursor);

    internal const uint WM_SETCURSOR = 0x0020;

    [LibraryImport(Kernel32, EntryPoint = "GetModuleHandleW")]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvStdcall)])]
    internal static partial nint GetModuleHandleW(nint lpModuleName);

    // -- gdi --

    private const string Gdi32 = "gdi32.dll";

    [LibraryImport(Gdi32)]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvStdcall)])]
    internal static partial nint CreateSolidBrush(uint crColor);

    [LibraryImport(Gdi32)]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvStdcall)])]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool DeleteObject(nint ho);

    // -- foreground window (for keyboard layout detection) --

    [LibraryImport(User32)]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvStdcall)])]
    internal static partial nint GetForegroundWindow();

    [LibraryImport(User32)]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvStdcall)])]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool GetWindowRect(nint hWnd, out WINRECT lpRect);

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

    [LibraryImport(User32, EntryPoint = "PostMessageW")]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvStdcall)])]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool PostMessage(nint hWnd, uint msg, nint wParam, nint lParam);

    // SetThreadExecutionState: prevents sleep/screensaver
    internal const uint ES_DISPLAY_REQUIRED = 0x00000002;

    [LibraryImport(Kernel32)]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvStdcall)])]
    internal static partial uint SetThreadExecutionState(uint esFlags);

    // -- clipboard --

    internal const uint CF_UNICODETEXT = 13;
    internal const uint CF_BITMAP = 2;
    internal const uint CF_DIB = 8;
    internal const uint CF_HDROP = 15;
    internal const uint GMEM_MOVEABLE = 0x0002;
    internal const uint GMEM_DDESHARE = 0x2000;

    [LibraryImport(User32, EntryPoint = "RegisterClipboardFormatW", StringMarshalling = StringMarshalling.Utf16)]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvStdcall)])]
    internal static partial uint RegisterClipboardFormat(string lpszFormat);

    [LibraryImport(User32)]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvStdcall)])]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool IsClipboardFormatAvailable(uint format);

    [LibraryImport(Kernel32)]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvStdcall)])]
    internal static partial nuint GlobalSize(nint hMem);

    [LibraryImport(User32, SetLastError = true)]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvStdcall)])]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool OpenClipboard(nint hWndNewOwner);

    [LibraryImport(User32)]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvStdcall)])]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool CloseClipboard();

    [LibraryImport(User32)]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvStdcall)])]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool EmptyClipboard();

    [LibraryImport(User32)]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvStdcall)])]
    internal static partial nint GetClipboardData(uint uFormat);

    [LibraryImport(User32)]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvStdcall)])]
    internal static partial nint SetClipboardData(uint uFormat, nint hMem);

    [LibraryImport(Kernel32)]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvStdcall)])]
    internal static partial nint GlobalAlloc(uint uFlags, nuint dwBytes);

    [LibraryImport(Kernel32)]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvStdcall)])]
    internal static partial nint GlobalLock(nint hMem);

    [LibraryImport(Kernel32)]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvStdcall)])]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool GlobalUnlock(nint hMem);

    [LibraryImport(Kernel32)]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvStdcall)])]
    internal static partial nint GlobalFree(nint hMem);

    // -- desktop --

    internal const uint GENERIC_READ = 0x80000000;
    internal const uint GENERIC_WRITE = 0x40000000;
    internal const uint DESKTOP_READOBJECTS = 0x0001;
    internal const uint DESKTOP_CREATEWINDOW = 0x0002;
    internal const uint DESKTOP_HOOKCONTROL = 0x0008;
    internal const uint DESKTOP_WRITEOBJECTS = 0x0080;
    internal const uint DF_ALLOWOTHERACCOUNTHOOK = 0x0001;
    internal const int UOI_NAME = 2;

    [LibraryImport(User32)]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvStdcall)])]
    internal static partial nint GetThreadDesktop(uint dwThreadId);

    [LibraryImport(User32, SetLastError = true)]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvStdcall)])]
    internal static partial nint OpenInputDesktop(uint dwFlags, [MarshalAs(UnmanagedType.Bool)] bool fInherit, uint dwDesiredAccess);

    [LibraryImport(User32)]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvStdcall)])]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool SetThreadDesktop(nint hDesktop);

    [LibraryImport(User32, EntryPoint = "GetUserObjectInformationW")]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvStdcall)])]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool GetUserObjectInformationW(nint hObj, int nIndex, nint pvInfo, uint nLength, out uint lpnLengthNeeded);

    [LibraryImport(User32)]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvStdcall)])]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool CloseDesktop(nint hDesktop);

    // -- window text --

    [LibraryImport(User32, EntryPoint = "SetWindowTextW", StringMarshalling = StringMarshalling.Utf16)]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvStdcall)])]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool SetWindowTextW(nint hWnd, string lpString);

    // -- common controls --

    private const string ComCtl32 = "comctl32.dll";

    internal const uint ICC_PROGRESS_CLASS = 0x00000020;
    internal const uint PBS_SMOOTH = 0x01;
    internal const uint PBM_SETRANGE32 = 0x0406;
    internal const uint PBM_SETPOS = 0x0402;
    internal const int WM_COMMAND = 0x0111;
    internal const int BN_CLICKED = 0;
    internal const uint BS_PUSHBUTTON = 0x00000000;
    internal const uint SS_LEFT = 0x00000000;
    internal const uint WS_CHILD = 0x40000000;
    internal const uint WS_VISIBLE = 0x10000000;
    internal const uint WS_CAPTION = 0x00C00000;
    internal const uint WS_SYSMENU = 0x00080000;
    internal const uint WS_OVERLAPPED = 0x00000000;

    [StructLayout(LayoutKind.Sequential)]
    internal struct INITCOMMONCONTROLSEX
    {
        internal uint dwSize;
        internal uint dwICC;
    }

    [LibraryImport(ComCtl32)]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvStdcall)])]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool InitCommonControlsEx(in INITCOMMONCONTROLSEX lpInitCtrls);

    // -- OLE (required for clipboard image interop) --

    private const string Ole32 = "ole32.dll";

    [LibraryImport(Ole32)]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvStdcall)])]
    internal static partial int OleInitialize(nint pvReserved);

    [LibraryImport(Ole32)]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvStdcall)])]
    internal static partial void OleUninitialize();

    [LibraryImport(Ole32)]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvStdcall)])]
    internal static partial int RegisterDragDrop(nint hwnd, nint pDropTarget);

    [LibraryImport(Ole32)]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvStdcall)])]
    internal static partial int RevokeDragDrop(nint hwnd);

#pragma warning disable SYSLIB1054
    // DllImport required — LibraryImport's new COM marshaller returns ComObject which can't cast to legacy IDataObject
    [DllImport(Ole32)]
    internal static extern int OleGetClipboard([MarshalAs(UnmanagedType.Interface)] out System.Runtime.InteropServices.ComTypes.IDataObject ppDataObj);

    [DllImport(Ole32)]
    internal static extern void ReleaseStgMedium(ref System.Runtime.InteropServices.ComTypes.STGMEDIUM pMedium);
#pragma warning restore SYSLIB1054

    // -- shell: drag-and-drop file query --

    private const string Shell32 = "shell32.dll";

    // pass 0xFFFFFFFF as iFile to query file count; otherwise retrieves the i-th path
    [LibraryImport(Shell32, EntryPoint = "DragQueryFileW")]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvStdcall)])]
    internal static unsafe partial uint DragQueryFileW(nint hDrop, uint iFile, char* lpszFile, uint cch);

    // -- shell: file operations (move/copy/delete with native conflict dialog) --

    internal const uint FO_MOVE = 0x0001;
    internal const ushort FOF_NOCONFIRMMKDIR = 0x0200;
    internal const ushort FOF_ALLOWUNDO = 0x0040;

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    internal struct SHFILEOPSTRUCTW
    {
        internal nint hwnd;
        internal uint wFunc;
        [MarshalAs(UnmanagedType.LPWStr)] internal string pFrom;
        [MarshalAs(UnmanagedType.LPWStr)] internal string pTo;
        internal ushort fFlags;
        [MarshalAs(UnmanagedType.Bool)] internal bool fAnyOperationsAborted;
        internal nint hNameMappings;
        [MarshalAs(UnmanagedType.LPWStr)] internal string? lpszProgressTitle;
    }

#pragma warning disable SYSLIB1054
    // DllImport required — struct contains LPWStr fields that LibraryImport cannot marshal
    [DllImport(Shell32, CharSet = CharSet.Unicode)]
    internal static extern int SHFileOperationW(ref SHFILEOPSTRUCTW lpFileOp);
#pragma warning restore SYSLIB1054

}

[UnmanagedFunctionPointer(CallingConvention.StdCall)]
internal delegate bool MonitorEnumProc(nint hMonitor, nint hdcMonitor, ref WINRECT lprcMonitor, nint dwData);

[UnmanagedFunctionPointer(CallingConvention.StdCall)]
internal delegate nint HookProc(int nCode, nint wParam, nint lParam);

[UnmanagedFunctionPointer(CallingConvention.StdCall)]
internal delegate nint WndProc(nint hWnd, uint msg, nint wParam, nint lParam);

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

[StructLayout(LayoutKind.Sequential)]
internal struct POINTL
{
    internal int x, y;
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

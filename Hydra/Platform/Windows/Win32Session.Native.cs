// mirrors the Win32 SDK headers (processthreadsapi.h, tlhelp32.h, securitybaseapi.h, sddl.h, synchapi.h, wtsapi32.h, userenv.h)
// ReSharper disable InconsistentNaming
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace Hydra.Platform.Windows;

internal static partial class Win32Session
{
    private const string Kernel32 = "kernel32.dll";
    private const string Advapi32 = "advapi32.dll";
    private const string Wtsapi32 = "wtsapi32.dll";
    private const string Userenv = "userenv.dll";

    [LibraryImport(Kernel32, EntryPoint = "WTSGetActiveConsoleSessionId")]
    private static partial uint NativeGetActiveConsoleSessionId();

    [LibraryImport(Wtsapi32, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool WTSQueryUserToken(uint sessionId, out SafeAccessTokenHandle phToken);

    [LibraryImport(Kernel32, SetLastError = true)]
    private static partial SafeFileHandle CreateToolhelp32Snapshot(uint dwFlags, uint processId);

    [LibraryImport(Kernel32, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool Process32FirstW(SafeHandle hSnapshot, ref ProcessEntry32W lppe);

    [LibraryImport(Kernel32, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool Process32NextW(SafeHandle hSnapshot, ref ProcessEntry32W lppe);

    [LibraryImport(Kernel32, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool ProcessIdToSessionId(uint dwProcessId, out uint pSessionId);

    [LibraryImport(Kernel32, SetLastError = true)]
    private static partial SafeProcessHandle OpenProcess(uint dwDesiredAccess, [MarshalAs(UnmanagedType.Bool)] bool bInheritHandle, uint dwProcessId);

    [LibraryImport(Advapi32, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool OpenProcessToken(SafeHandle processHandle, uint desiredAccess, out SafeAccessTokenHandle tokenHandle);

    [LibraryImport(Advapi32, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool DuplicateTokenEx(SafeHandle hExistingToken, uint dwDesiredAccess,
        IntPtr lpTokenAttributes, int impersonationLevel, int tokenType, out SafeAccessTokenHandle phNewToken);

    [LibraryImport(Advapi32, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool SetTokenInformation(SafeHandle tokenHandle, int tokenInformationClass,
        ref uint tokenInformation, uint tokenInformationLength);

    // lpCommandLine is a writable, NUL-terminated buffer: CreateProcessAsUserW may modify it in place
    [LibraryImport(Advapi32, EntryPoint = "CreateProcessAsUserW", SetLastError = true, StringMarshalling = StringMarshalling.Utf16)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool CreateProcessAsUser(SafeHandle hToken,
        string? lpApplicationName,
        [In, Out] char[] lpCommandLine,
        IntPtr lpProcessAttributes, IntPtr lpThreadAttributes,
        [MarshalAs(UnmanagedType.Bool)] bool bInheritHandles, uint dwCreationFlags, IntPtr lpEnvironment,
        string? lpCurrentDirectory,
        ref STARTUPINFOW lpStartupInfo, out PROCESS_INFORMATION lpProcessInformation);

    [LibraryImport(Userenv, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool CreateEnvironmentBlock(out IntPtr lpEnvironment, SafeHandle hToken, [MarshalAs(UnmanagedType.Bool)] bool bInherit);

    [LibraryImport(Userenv)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool DestroyEnvironmentBlock(IntPtr lpEnvironment);

    [LibraryImport(Kernel32, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool GetExitCodeProcess(SafeHandle hProcess, out uint lpExitCode);

    [LibraryImport(Kernel32, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool TerminateProcess(SafeHandle hProcess, uint uExitCode);

    [LibraryImport(Kernel32, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool CloseHandle(IntPtr hObject);

    [LibraryImport(Kernel32, SetLastError = true, StringMarshalling = StringMarshalling.Utf16)]
    private static partial SafeFileHandle CreateEventW(ref SECURITY_ATTRIBUTES lpEventAttributes,
        [MarshalAs(UnmanagedType.Bool)] bool bManualReset, [MarshalAs(UnmanagedType.Bool)] bool initialState, string? lpName);

    [LibraryImport(Kernel32, SetLastError = true, StringMarshalling = StringMarshalling.Utf16)]
    private static partial SafeFileHandle OpenEventW(uint dwDesiredAccess, [MarshalAs(UnmanagedType.Bool)] bool bInheritHandle, string lpName);

    [LibraryImport(Kernel32, EntryPoint = "SetEvent")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool SetEventHandle(SafeHandle hEvent);

    [LibraryImport(Kernel32, EntryPoint = "ResetEvent")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool ResetEventHandle(SafeHandle hEvent);

    [LibraryImport(Kernel32)]
    private static partial uint WaitForSingleObject(SafeHandle hHandle, uint dwMilliseconds);

    [LibraryImport(Advapi32, EntryPoint = "ConvertStringSecurityDescriptorToSecurityDescriptorW", SetLastError = true, StringMarshalling = StringMarshalling.Utf16)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool ConvertStringSecurityDescriptorToSecurityDescriptor(
        string stringSd, uint revision, out IntPtr sd, out uint sdSize);

    [LibraryImport(Kernel32)]
    private static partial IntPtr LocalFree(IntPtr hMem);

    // -- structs --

    [StructLayout(LayoutKind.Sequential)]
    private struct SECURITY_ATTRIBUTES
    {
        internal uint nLength;
        internal IntPtr lpSecurityDescriptor;
        internal int bInheritHandle;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct ProcessEntry32W
    {
        internal uint dwSize;
        internal uint cntUsage;
        internal uint th32ProcessID;
        internal nuint th32DefaultHeapID;
        internal uint th32ModuleID;
        internal uint cntThreads;
        internal uint th32ParentProcessID;
        internal int pcPriClassBase;
        internal uint dwFlags;
        internal WideChars260 szExeFile;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct STARTUPINFOW
    {
        internal uint cb;
        internal IntPtr lpReserved;
        internal IntPtr lpDesktop;
        internal IntPtr lpTitle;
        internal uint dwX, dwY, dwXSize, dwYSize;
        internal uint dwXCountChars, dwYCountChars;
        internal uint dwFillAttribute, dwFlags;
        internal ushort wShowWindow, cbReserved2;
        internal IntPtr lpReserved2;
        internal IntPtr hStdInput, hStdOutput, hStdError;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct PROCESS_INFORMATION
    {
        internal IntPtr hProcess;
        internal IntPtr hThread;
        internal uint dwProcessId;
        internal uint dwThreadId;
    }
}

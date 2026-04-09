using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using Microsoft.Win32.SafeHandles;
// ReSharper disable InconsistentNaming

namespace Hydra.Platform.Windows;

/// <summary>Represents a child process launched into a user session.</summary>
[SupportedOSPlatform("windows")]
internal sealed class ChildProcess(SafeProcessHandle handle, uint pid) : IDisposable
{
    internal SafeProcessHandle Handle { get; } = handle;
    internal uint Pid { get; } = pid;
    public void Dispose() => Handle.Dispose();
}

/// <summary>P/Invoke helpers for Windows session and process management.</summary>
[SupportedOSPlatform("windows")]
internal static partial class Win32Session
{
    internal const uint NoSession = uint.MaxValue;
    internal const uint Infinite = 0xFFFFFFFF;

    // -- session --

    internal static uint GetActiveConsoleSessionId()
        => NativeGetActiveConsoleSessionId();

    /// <summary>Launches <paramref name="exePath"/> with <paramref name="extraArgs"/> in the given session.</summary>
    internal static ChildProcess LaunchInSession(uint sessionId, string exePath, string extraArgs)
    {
        using var token = AcquireSessionToken(sessionId);

        // allow child to interact with secure desktop (lock screen, UAC prompts)
        uint uiAccess = 1;
        _ = SetTokenInformation(token, 26 /*TokenUIAccess*/, ref uiAccess, sizeof(uint));

        _ = CreateEnvironmentBlock(out var envBlock, token, false);
        try
        {
            var si = new STARTUPINFOW
            {
                cb = (uint)Marshal.SizeOf<STARTUPINFOW>(),
                lpDesktop = "winsta0\\Default",
            };

            var cmdLine = new System.Text.StringBuilder($"\"{exePath}\" {extraArgs}");
            const uint createNoWindow = 0x00000010;
            const uint createUnicodeEnv = 0x00000400;

            if (!CreateProcessAsUser(token, null, cmdLine, IntPtr.Zero, IntPtr.Zero,
                    false, createNoWindow | createUnicodeEnv,
                    envBlock, null, ref si, out var pi))
                throw new Win32Exception(Marshal.GetLastWin32Error(), "CreateProcessAsUser failed");

            _ = CloseHandle(pi.hThread);
            return new ChildProcess(new SafeProcessHandle(pi.hProcess, ownsHandle: true), pi.dwProcessId);
        }
        finally
        {
            if (envBlock != IntPtr.Zero) _ = DestroyEnvironmentBlock(envBlock);
        }
    }

    internal static bool HasProcessExited(SafeProcessHandle handle)
    {
        if (handle.IsInvalid || handle.IsClosed) return true;
        if (!GetExitCodeProcess(handle, out var code)) return true;
        return code != 259; // STILL_ACTIVE
    }

    internal static void KillProcess(SafeProcessHandle handle)
    {
        if (!handle.IsInvalid && !handle.IsClosed)
            _ = TerminateProcess(handle, 0);
    }

    // -- named events --

    internal static SafeFileHandle CreateGlobalEvent(string name, bool manualReset)
    {
        // SDDL: grant full event access to Everyone (WD) so user-session processes can open it
        _ = ConvertStringSecurityDescriptorToSecurityDescriptor("D:(A;;0x001F0003;;;WD)", 1, out var sd, out _);
        try
        {
            var sa = new SECURITY_ATTRIBUTES { nLength = (uint)Marshal.SizeOf<SECURITY_ATTRIBUTES>(), lpSecurityDescriptor = sd };
            var handle = CreateEventW(ref sa, manualReset, initialState: false, $"Global\\{name}");
            if (handle.IsInvalid) throw new Win32Exception(Marshal.GetLastWin32Error(), $"CreateEvent({name}) failed");
            return handle;
        }
        finally
        {
            if (sd != IntPtr.Zero) _ = LocalFree(sd);
        }
    }

    internal static SafeFileHandle? OpenGlobalEvent(string name)
    {
        const uint synchronize = 0x00100000;
        const uint eventModifyState = 0x0002;
        var handle = OpenEventW(synchronize | eventModifyState, bInheritHandle: false, $"Global\\{name}");
        return handle.IsInvalid ? null : handle;
    }

    internal static void SignalGlobalEvent(string name)
    {
        using var handle = OpenGlobalEvent(name);
        if (handle != null) SetEventHandle(handle);
    }

    internal static bool SignalEvent(SafeHandle handle) => SetEventHandle(handle);

    internal static bool ResetGlobalEvent(SafeHandle handle) => ResetEventHandle(handle);

    internal static bool WaitForEvent(SafeHandle handle, uint timeoutMs)
        => WaitForSingleObject(handle, timeoutMs) == 0; // WAIT_OBJECT_0

    // -- private helpers --

    private static SafeAccessTokenHandle AcquireSessionToken(uint sessionId)
    {
        // try the logged-in user's token first (works when user is at the desktop or lock screen)
        if (WTSQueryUserToken(sessionId, out var token) && !token.IsInvalid)
            return token;

        // fall back to winlogon.exe token (for pre-login or when WTSQueryUserToken is not available)
        token.Dispose();
        return FindWinlogonToken(sessionId)
            ?? throw new InvalidOperationException($"no token available for session {sessionId}");
    }

    private static SafeAccessTokenHandle? FindWinlogonToken(uint sessionId)
    {
        using var snapshot = CreateToolhelp32Snapshot(0x00000002u /*TH32CS_SNAPPROCESS*/, 0);
        if (snapshot.IsInvalid) return null;

        var entry = new PROCESSENTRY32W { dwSize = (uint)Marshal.SizeOf<PROCESSENTRY32W>() };
        if (!Process32FirstW(snapshot, ref entry)) return null;

        do
        {
            if (!entry.szExeFile.Equals("winlogon.exe", StringComparison.OrdinalIgnoreCase)) continue;
            if (!ProcessIdToSessionId(entry.th32ProcessID, out var procSession)) continue;
            if (procSession != sessionId) continue;

            using var proc = OpenProcess(0x00000400u /*PROCESS_QUERY_INFORMATION*/, bInheritHandle: false, entry.th32ProcessID);
            if (proc.IsInvalid) continue;

            if (!OpenProcessToken(proc, 0x0002u /*TOKEN_DUPLICATE*/, out var procToken)) continue;
            using (procToken)
            {
                const uint tokenAllAccess = 0xF01FF;
                if (!DuplicateTokenEx(procToken, tokenAllAccess, IntPtr.Zero,
                        2 /*SecurityImpersonation*/, 1 /*TokenPrimary*/, out var dup)) continue;
                return dup;
            }
        }
        while (Process32NextW(snapshot, ref entry));

        return null;
    }

    // -- P/Invoke --

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
    private static partial SafeFileHandle CreateToolhelp32Snapshot(uint dwFlags, uint th32ProcessID);

#pragma warning disable SYSLIB1054
    [DllImport(Kernel32, SetLastError = true, CharSet = CharSet.Unicode)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool Process32FirstW(SafeHandle hSnapshot, ref PROCESSENTRY32W lppe);

    [DllImport(Kernel32, SetLastError = true, CharSet = CharSet.Unicode)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool Process32NextW(SafeHandle hSnapshot, ref PROCESSENTRY32W lppe);
#pragma warning restore SYSLIB1054

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
        IntPtr lpTokenAttributes, int ImpersonationLevel, int TokenType, out SafeAccessTokenHandle phNewToken);

    [LibraryImport(Advapi32, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool SetTokenInformation(SafeHandle tokenHandle, int tokenInformationClass,
        ref uint tokenInformation, uint tokenInformationLength);

#pragma warning disable SYSLIB1054
    [DllImport(Advapi32, SetLastError = true, CharSet = CharSet.Unicode)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CreateProcessAsUser(SafeHandle hToken,
        [MarshalAs(UnmanagedType.LPWStr)] string? lpApplicationName,
        System.Text.StringBuilder lpCommandLine,
        IntPtr lpProcessAttributes, IntPtr lpThreadAttributes,
        bool bInheritHandles, uint dwCreationFlags, IntPtr lpEnvironment,
        [MarshalAs(UnmanagedType.LPWStr)] string? lpCurrentDirectory,
        ref STARTUPINFOW lpStartupInfo, out PROCESS_INFORMATION lpProcessInformation);
#pragma warning restore SYSLIB1054

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

#pragma warning disable SYSLIB1054
    [DllImport(Kernel32, SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern SafeFileHandle CreateEventW(ref SECURITY_ATTRIBUTES lpEventAttributes,
        bool bManualReset, bool initialState, string? lpName);
#pragma warning restore SYSLIB1054

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

    [LibraryImport(Advapi32, SetLastError = true, StringMarshalling = StringMarshalling.Utf16)]
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
        [MarshalAs(UnmanagedType.Bool)]
        internal bool bInheritHandle;
    }

#pragma warning disable SYSLIB1054
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct PROCESSENTRY32W
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
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)]
        internal string szExeFile;
    }
#pragma warning restore SYSLIB1054

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct STARTUPINFOW
    {
        internal uint cb;
        internal string? lpReserved;
        internal string? lpDesktop;
        internal string? lpTitle;
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

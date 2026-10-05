using System.ComponentModel;
using Cathedral.Extensions;
using System.IO.Pipes;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Security.AccessControl;
using System.Security.Principal;
using Microsoft.Win32.SafeHandles;

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

        // build environment from user's profile so %APPDATA% etc. point to user paths, not SYSTEM's
        using var envToken = AcquireUserToken(sessionId);
        _ = CreateEnvironmentBlock(out var envBlock, envToken ?? token, false);
        var desktop = Marshal.StringToHGlobalUni("winsta0\\Default");
        try
        {
            var si = new STARTUPINFOW
            {
                cb = (uint)Unsafe.SizeOf<STARTUPINFOW>(),
                lpDesktop = desktop,
                dwFlags = 0x00000001,  // STARTF_USESHOWWINDOW
                wShowWindow = 0,       // SW_HIDE
            };

            var cmdLine = $"\"{exePath}\" {extraArgs}\0".ToCharArray();
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
            Marshal.FreeHGlobal(desktop);
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

    /// <summary>Builds a private management-pipe ACL for the service child and active desktop user.</summary>
    internal static PipeSecurity CreateManagementPipeSecurity()
    {
        var system = new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null);
        var security = new PipeSecurity();
        security.SetAccessRuleProtection(isProtected: true, preserveInheritance: false);
        security.SetOwner(system);
        security.AddAccessRule(new PipeAccessRule(system, PipeAccessRights.FullControl, AccessControlType.Allow));

        if (ActiveConsoleUser() is { } user)
            security.AddAccessRule(new PipeAccessRule(user, PipeAccessRights.FullControl, AccessControlType.Allow));
        return security;
    }

    /// <summary>
    /// The user at the console, whom Hydra trusts with its management pipe and its private files. Null without
    /// a signed-in console user, or without the privilege to ask, which only SYSTEM holds.
    /// </summary>
    internal static SecurityIdentifier? ActiveConsoleUser()
    {
        var sessionId = GetActiveConsoleSessionId();
        if (sessionId == NoSession) return null;

        using var token = AcquireUserToken(sessionId);
        if (token == null) return null;
        using var identity = new WindowsIdentity(token.DangerousGetHandle());
        return identity.User;
    }

    // -- named events --

    internal static SafeFileHandle CreateGlobalEvent(string name, bool manualReset)
    {
        // SDDL: grant full event access to Everyone (WD) so user-session processes can open it
        _ = ConvertStringSecurityDescriptorToSecurityDescriptor("D:(A;;0x001F0003;;;WD)", 1, out var sd, out _);
        try
        {
            var sa = new SECURITY_ATTRIBUTES { nLength = (uint)Unsafe.SizeOf<SECURITY_ATTRIBUTES>(), lpSecurityDescriptor = sd };
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
        // use SYSTEM token (winlogon.exe) — required for OpenInputDesktop/SetThreadDesktop on secure desktops
        return FindWinlogonToken(sessionId)
            ?? AcquireUserToken(sessionId)
            ?? throw new InvalidOperationException($"no token available for session {sessionId}");
    }

    private static SafeAccessTokenHandle? AcquireUserToken(uint sessionId)
    {
        if (WTSQueryUserToken(sessionId, out var token) && !token.IsInvalid)
            return token;
        token.Dispose();
        return null;
    }

    // the ids of every running process whose executable has this file name
    internal static List<uint> ProcessIdsNamed(string exeName)
    {
        var pids = new List<uint>();
        using var snapshot = CreateToolhelp32Snapshot(0x00000002u /*TH32CS_SNAPPROCESS*/, 0);
        if (snapshot.IsInvalid) return pids;

        var entry = new ProcessEntry32W { dwSize = (uint)Unsafe.SizeOf<ProcessEntry32W>() };
        if (!Process32FirstW(snapshot, ref entry)) return pids;
        do
        {
            if (entry.szExeFile.ToString().EqualsIgnoreCase(exeName)) pids.Add(entry.th32ProcessID);
        }
        while (Process32NextW(snapshot, ref entry));
        return pids;
    }

    private static SafeAccessTokenHandle? FindWinlogonToken(uint sessionId)
    {
        foreach (var pid in ProcessIdsNamed("winlogon.exe"))
        {
            if (!ProcessIdToSessionId(pid, out var procSession)) continue;
            if (procSession != sessionId) continue;

            using var proc = OpenProcess(0x00000400u /*PROCESS_QUERY_INFORMATION*/, bInheritHandle: false, pid);
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

        return null;
    }
}

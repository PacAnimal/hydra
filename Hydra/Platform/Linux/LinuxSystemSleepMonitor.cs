using System.ComponentModel;
using System.Runtime.InteropServices;
using Microsoft.Extensions.Logging;
using Microsoft.Win32.SafeHandles;

namespace Hydra.Platform.Linux;

// systemd-logind emits PrepareForSleep before and after suspend. A delay inhibitor keeps the
// pre-suspend window open until Hydra has disconnected the relay (or logind's own deadline expires).
internal sealed partial class LinuxSystemSleepMonitor : ThreadedSleepMonitor
{
    private const ulong PollIntervalMicroseconds = 1_000_000;
    private const string SleepMatch =
        "type='signal',sender='org.freedesktop.login1',path='/org/freedesktop/login1'," +
        "interface='org.freedesktop.login1.Manager',member='PrepareForSleep'";

    private readonly SystemdNative.BusMessageHandler _messageHandler;
    private readonly Lock _inhibitorLock = new();
    private SafeFileHandle? _inhibitor;

    public LinuxSystemSleepMonitor(
        SystemSleepCoordinator coordinator,
        ILogger<LinuxSystemSleepMonitor> log) : base(coordinator, log)
    {
        _messageHandler = OnPrepareForSleep;
    }

    protected override string NotificationSource => "systemd-logind sleep notifications";
    protected override string MonitorName => "Linux system sleep monitor";

    protected override void RunLoop(TaskCompletionSource<bool> ready)
    {
        nint bus = nint.Zero;
        nint slot = nint.Zero;
        try
        {
            ThrowIfFailed(SystemdNative.sd_bus_default_system(out bus), "connect to the system bus");
            ThrowIfFailed(SystemdNative.sd_bus_add_match(bus, out slot, SleepMatch, _messageHandler, nint.Zero),
                "subscribe to PrepareForSleep");

            TryAcquireDelayInhibitor(bus);
            ready.TrySetResult(true);
            Log.LogInformation("Watching Linux system sleep and wake notifications through systemd-logind");

            while (!Stopping)
            {
                int processed;
                do
                {
                    processed = SystemdNative.sd_bus_process(bus, nint.Zero);
                    ThrowIfFailed(processed, "process system bus messages");
                } while (processed > 0 && !Stopping);

                if (!Stopping)
                    ThrowIfFailed(SystemdNative.sd_bus_wait(bus, PollIntervalMicroseconds),
                        "wait for system bus messages");
            }
        }
        catch (Exception ex)
        {
            ready.TrySetResult(false);
            Log.LogWarning(ex, "Linux system sleep monitor stopped unexpectedly");
        }
        finally
        {
            ReleaseDelayInhibitor();
            if (slot != nint.Zero) _ = SystemdNative.sd_bus_slot_unref(slot);
            if (bus != nint.Zero) _ = SystemdNative.sd_bus_unref(bus);
            ready.TrySetResult(false);
        }
    }

    private int OnPrepareForSleep(nint message, nint _, nint __)
    {
        try
        {
            var result = SystemdNative.sd_bus_message_read_basic(message, (byte)'b', out var preparing);
            ThrowIfFailed(result, "read PrepareForSleep payload");

            if (preparing != 0)
            {
                Coordinator.PrepareForSleepBlocking();
                // Releasing the delay inhibitor tells logind Hydra has finished its pre-sleep work.
                ReleaseDelayInhibitor();
            }
            else
            {
                Coordinator.ResumeAfterSleep();
                var bus = SystemdNative.sd_bus_message_get_bus(message);
                if (bus != nint.Zero) TryAcquireDelayInhibitor(bus);
            }
            return 0;
        }
        catch (Exception ex)
        {
            // Do not let a managed exception cross the native callback boundary. logind will still
            // enforce its own delay deadline if the inhibitor could not be released here.
            Log.LogWarning(ex, "Failed to handle Linux PrepareForSleep notification");
            ReleaseDelayInhibitor();
            return 0;
        }
    }

    private void TryAcquireDelayInhibitor(nint bus)
    {
        try
        {
            AcquireDelayInhibitor(bus);
        }
        catch (Exception ex)
        {
            Log.LogWarning(ex,
                "Could not take the systemd-logind delay inhibitor; sleep notification handling is best effort");
        }
    }

    private void AcquireDelayInhibitor(nint bus)
    {
        lock (_inhibitorLock)
        {
            if (_inhibitor is { IsInvalid: false, IsClosed: false }) return;

            nint call = nint.Zero;
            nint reply = nint.Zero;
            var error = default(SystemdNative.SdBusError);
            try
            {
                ThrowIfFailed(SystemdNative.sd_bus_message_new_method_call(
                    bus,
                    out call,
                    "org.freedesktop.login1",
                    "/org/freedesktop/login1",
                    "org.freedesktop.login1.Manager",
                    "Inhibit"), "create logind Inhibit call");
                AppendString(call, "sleep");
                AppendString(call, "Hydra");
                AppendString(call, "Closing relay connection before system sleep");
                AppendString(call, "delay");
                ThrowIfFailed(SystemdNative.sd_bus_call(bus, call, 5_000_000, ref error, out reply),
                    "take logind delay inhibitor", error);
                ThrowIfFailed(SystemdNative.sd_bus_message_read_basic(reply, (byte)'h', out var borrowedFd),
                    "read logind inhibitor descriptor");

                var ownedFd = SystemdNative.Dup(borrowedFd);
                if (ownedFd < 0) throw new Win32Exception(Marshal.GetLastPInvokeError(), "dup inhibitor descriptor");
                _inhibitor = new SafeFileHandle(ownedFd, ownsHandle: true);
            }
            finally
            {
                SystemdNative.sd_bus_error_free(ref error);
                if (reply != nint.Zero) _ = SystemdNative.sd_bus_message_unref(reply);
                if (call != nint.Zero) _ = SystemdNative.sd_bus_message_unref(call);
            }
        }
    }

    private static void AppendString(nint message, string value) =>
        ThrowIfFailed(SystemdNative.sd_bus_message_append_basic(message, (byte)'s', value),
            "append logind Inhibit argument");

    private void ReleaseDelayInhibitor()
    {
        SafeFileHandle? inhibitor;
        lock (_inhibitorLock)
        {
            inhibitor = _inhibitor;
            _inhibitor = null;
        }
        inhibitor?.Dispose();
    }

    private static void ThrowIfFailed(int result, string operation, SystemdNative.SdBusError error = default)
    {
        if (result >= 0) return;
        var detail = error.Message == nint.Zero ? null : Marshal.PtrToStringUTF8(error.Message);
        throw new Win32Exception(-result, detail == null ? operation : $"{operation}: {detail}");
    }

    private static partial class SystemdNative
    {
        private const string LibSystemd = "libsystemd.so.0";

        [StructLayout(LayoutKind.Sequential)]
        internal struct SdBusError
        {
            internal nint Name;
            internal nint Message;
            private readonly int _needFree;
        }

        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        internal delegate int BusMessageHandler(nint message, nint userData, nint error);

        [LibraryImport(LibSystemd)]
        [UnmanagedCallConv(CallConvs = [typeof(System.Runtime.CompilerServices.CallConvCdecl)])]
        internal static partial int sd_bus_default_system(out nint bus);

        [LibraryImport(LibSystemd)]
        [UnmanagedCallConv(CallConvs = [typeof(System.Runtime.CompilerServices.CallConvCdecl)])]
        internal static partial int sd_bus_add_match(
            nint bus,
            out nint slot,
            [MarshalAs(UnmanagedType.LPUTF8Str)] string match,
            BusMessageHandler callback,
            nint userData);

        [LibraryImport(LibSystemd)]
        [UnmanagedCallConv(CallConvs = [typeof(System.Runtime.CompilerServices.CallConvCdecl)])]
        internal static partial int sd_bus_process(nint bus, nint message);

        [LibraryImport(LibSystemd)]
        [UnmanagedCallConv(CallConvs = [typeof(System.Runtime.CompilerServices.CallConvCdecl)])]
        internal static partial int sd_bus_wait(nint bus, ulong timeoutMicroseconds);

        [LibraryImport(LibSystemd)]
        [UnmanagedCallConv(CallConvs = [typeof(System.Runtime.CompilerServices.CallConvCdecl)])]
        internal static partial nint sd_bus_slot_unref(nint slot);

        [LibraryImport(LibSystemd)]
        [UnmanagedCallConv(CallConvs = [typeof(System.Runtime.CompilerServices.CallConvCdecl)])]
        internal static partial nint sd_bus_unref(nint bus);

        [LibraryImport(LibSystemd)]
        [UnmanagedCallConv(CallConvs = [typeof(System.Runtime.CompilerServices.CallConvCdecl)])]
        internal static partial int sd_bus_message_new_method_call(
            nint bus,
            out nint message,
            [MarshalAs(UnmanagedType.LPUTF8Str)] string destination,
            [MarshalAs(UnmanagedType.LPUTF8Str)] string path,
            [MarshalAs(UnmanagedType.LPUTF8Str)] string interfaceName,
            [MarshalAs(UnmanagedType.LPUTF8Str)] string member);

        [LibraryImport(LibSystemd)]
        [UnmanagedCallConv(CallConvs = [typeof(System.Runtime.CompilerServices.CallConvCdecl)])]
        internal static partial int sd_bus_message_append_basic(
            nint message,
            byte type,
            [MarshalAs(UnmanagedType.LPUTF8Str)] string value);

        [LibraryImport(LibSystemd)]
        [UnmanagedCallConv(CallConvs = [typeof(System.Runtime.CompilerServices.CallConvCdecl)])]
        internal static partial int sd_bus_call(
            nint bus,
            nint message,
            ulong timeoutMicroseconds,
            ref SdBusError error,
            out nint reply);

        [LibraryImport(LibSystemd)]
        [UnmanagedCallConv(CallConvs = [typeof(System.Runtime.CompilerServices.CallConvCdecl)])]
        internal static partial int sd_bus_message_read_basic(nint message, byte type, out int value);

        [LibraryImport(LibSystemd)]
        [UnmanagedCallConv(CallConvs = [typeof(System.Runtime.CompilerServices.CallConvCdecl)])]
        internal static partial nint sd_bus_message_get_bus(nint message);

        [LibraryImport(LibSystemd)]
        [UnmanagedCallConv(CallConvs = [typeof(System.Runtime.CompilerServices.CallConvCdecl)])]
        internal static partial nint sd_bus_message_unref(nint message);

        [LibraryImport(LibSystemd)]
        [UnmanagedCallConv(CallConvs = [typeof(System.Runtime.CompilerServices.CallConvCdecl)])]
        internal static partial void sd_bus_error_free(ref SdBusError error);

        [LibraryImport("libc", EntryPoint = "dup", SetLastError = true)]
        [UnmanagedCallConv(CallConvs = [typeof(System.Runtime.CompilerServices.CallConvCdecl)])]
        internal static partial int Dup(int oldFileDescriptor);
    }
}

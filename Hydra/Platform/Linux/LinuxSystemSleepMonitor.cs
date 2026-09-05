using System.ComponentModel;
using System.Runtime.InteropServices;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Win32.SafeHandles;

namespace Hydra.Platform.Linux;

// systemd-logind emits PrepareForSleep before and after suspend. A delay inhibitor keeps the
// pre-suspend window open until Hydra has disconnected the relay (or logind's own deadline expires).
internal sealed class LinuxSystemSleepMonitor : IHostedService, IDisposable
{
    private const ulong PollIntervalMicroseconds = 1_000_000;
    private static readonly TimeSpan RelayCloseTimeout = TimeSpan.FromSeconds(5);
    private const string SleepMatch =
        "type='signal',sender='org.freedesktop.login1',path='/org/freedesktop/login1'," +
        "interface='org.freedesktop.login1.Manager',member='PrepareForSleep'";

    private readonly SystemSleepCoordinator _coordinator;
    private readonly ILogger<LinuxSystemSleepMonitor> _log;
    private readonly SystemdNative.BusMessageHandler _messageHandler;
    private readonly Lock _inhibitorLock = new();
    private Thread? _thread;
    private SafeFileHandle? _inhibitor;
    private volatile bool _stopping;

    public LinuxSystemSleepMonitor(
        SystemSleepCoordinator coordinator,
        ILogger<LinuxSystemSleepMonitor> log)
    {
        _coordinator = coordinator;
        _log = log;
        _messageHandler = OnPrepareForSleep;
    }

    public async Task StartAsync(CancellationToken cancellationToken)
    {
        if (!_coordinator.Enabled) return;

        var ready = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        _thread = new Thread(() => RunMessageLoop(ready))
        {
            IsBackground = true,
            Name = "HydraSystemSleep"
        };
        _thread.Start();

        if (!await ready.Task.WaitAsync(cancellationToken))
            _log.LogWarning("systemd-logind sleep notifications are unavailable; relay sleep suspension is disabled");
    }

    private void RunMessageLoop(TaskCompletionSource<bool> ready)
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
            _log.LogInformation("Watching Linux system sleep and wake notifications through systemd-logind");

            while (!_stopping)
            {
                int processed;
                do
                {
                    processed = SystemdNative.sd_bus_process(bus, nint.Zero);
                    ThrowIfFailed(processed, "process system bus messages");
                } while (processed > 0 && !_stopping);

                if (!_stopping)
                    ThrowIfFailed(SystemdNative.sd_bus_wait(bus, PollIntervalMicroseconds),
                        "wait for system bus messages");
            }
        }
        catch (Exception ex)
        {
            ready.TrySetResult(false);
            _log.LogWarning(ex, "Linux system sleep monitor stopped unexpectedly");
        }
        finally
        {
            ReleaseDelayInhibitor();
            if (slot != nint.Zero) SystemdNative.sd_bus_slot_unref(slot);
            if (bus != nint.Zero) SystemdNative.sd_bus_unref(bus);
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
                using var timeout = new CancellationTokenSource(RelayCloseTimeout);
                _coordinator.PrepareForSleepAsync(timeout.Token).GetAwaiter().GetResult();
                // Releasing the delay inhibitor tells logind Hydra has finished its pre-sleep work.
                ReleaseDelayInhibitor();
            }
            else
            {
                _coordinator.ResumeAfterSleep();
                var bus = SystemdNative.sd_bus_message_get_bus(message);
                if (bus != nint.Zero) TryAcquireDelayInhibitor(bus);
            }
            return 0;
        }
        catch (Exception ex)
        {
            // Do not let a managed exception cross the native callback boundary. logind will still
            // enforce its own delay deadline if the inhibitor could not be released here.
            _log.LogWarning(ex, "Failed to handle Linux PrepareForSleep notification");
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
            _log.LogWarning(ex,
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

                var ownedFd = SystemdNative.dup(borrowedFd);
                if (ownedFd < 0) throw new Win32Exception(Marshal.GetLastPInvokeError(), "dup inhibitor descriptor");
                _inhibitor = new SafeFileHandle(ownedFd, ownsHandle: true);
            }
            finally
            {
                SystemdNative.sd_bus_error_free(ref error);
                if (reply != nint.Zero) SystemdNative.sd_bus_message_unref(reply);
                if (call != nint.Zero) SystemdNative.sd_bus_message_unref(call);
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

    public Task StopAsync(CancellationToken cancellationToken)
    {
        _stopping = true;
        var thread = _thread;
        _thread = null;
        if (thread?.Join(RelayCloseTimeout + TimeSpan.FromSeconds(2)) == false)
            _log.LogWarning("Linux system sleep monitor did not stop before its shutdown deadline");
        return Task.CompletedTask;
    }

    public void Dispose() => StopAsync(CancellationToken.None).GetAwaiter().GetResult();

    private static void ThrowIfFailed(int result, string operation, SystemdNative.SdBusError error = default)
    {
        if (result >= 0) return;
        var detail = error.Message == nint.Zero ? null : Marshal.PtrToStringUTF8(error.Message);
        throw new Win32Exception(-result, detail == null ? operation : $"{operation}: {detail}");
    }

    private static class SystemdNative
    {
        private const string LibSystemd = "libsystemd.so.0";

        [StructLayout(LayoutKind.Sequential)]
        internal struct SdBusError
        {
            internal nint Name;
            internal nint Message;
            private int _needFree;
        }

        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        internal delegate int BusMessageHandler(nint message, nint userData, nint error);

        [DllImport(LibSystemd, CallingConvention = CallingConvention.Cdecl)]
        internal static extern int sd_bus_default_system(out nint bus);

        [DllImport(LibSystemd, CallingConvention = CallingConvention.Cdecl)]
        internal static extern int sd_bus_add_match(
            nint bus,
            out nint slot,
            [MarshalAs(UnmanagedType.LPUTF8Str)] string match,
            BusMessageHandler callback,
            nint userData);

        [DllImport(LibSystemd, CallingConvention = CallingConvention.Cdecl)]
        internal static extern int sd_bus_process(nint bus, nint message);

        [DllImport(LibSystemd, CallingConvention = CallingConvention.Cdecl)]
        internal static extern int sd_bus_wait(nint bus, ulong timeoutMicroseconds);

        [DllImport(LibSystemd, CallingConvention = CallingConvention.Cdecl)]
        internal static extern nint sd_bus_slot_unref(nint slot);

        [DllImport(LibSystemd, CallingConvention = CallingConvention.Cdecl)]
        internal static extern nint sd_bus_unref(nint bus);

        [DllImport(LibSystemd, CallingConvention = CallingConvention.Cdecl)]
        internal static extern int sd_bus_message_new_method_call(
            nint bus,
            out nint message,
            [MarshalAs(UnmanagedType.LPUTF8Str)] string destination,
            [MarshalAs(UnmanagedType.LPUTF8Str)] string path,
            [MarshalAs(UnmanagedType.LPUTF8Str)] string interfaceName,
            [MarshalAs(UnmanagedType.LPUTF8Str)] string member);

        [DllImport(LibSystemd, CallingConvention = CallingConvention.Cdecl)]
        internal static extern int sd_bus_message_append_basic(
            nint message,
            byte type,
            [MarshalAs(UnmanagedType.LPUTF8Str)] string value);

        [DllImport(LibSystemd, CallingConvention = CallingConvention.Cdecl)]
        internal static extern int sd_bus_call(
            nint bus,
            nint message,
            ulong timeoutMicroseconds,
            ref SdBusError error,
            out nint reply);

        [DllImport(LibSystemd, CallingConvention = CallingConvention.Cdecl)]
        internal static extern int sd_bus_message_read_basic(nint message, byte type, out int value);

        [DllImport(LibSystemd, CallingConvention = CallingConvention.Cdecl)]
        internal static extern nint sd_bus_message_get_bus(nint message);

        [DllImport(LibSystemd, CallingConvention = CallingConvention.Cdecl)]
        internal static extern nint sd_bus_message_unref(nint message);

        [DllImport(LibSystemd, CallingConvention = CallingConvention.Cdecl)]
        internal static extern void sd_bus_error_free(ref SdBusError error);

        [DllImport("libc", SetLastError = true, CallingConvention = CallingConvention.Cdecl)]
        internal static extern int dup(int oldFileDescriptor);
    }
}

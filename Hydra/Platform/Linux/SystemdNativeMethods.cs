// mirrors <systemd/sd-bus.h>, and dup from <unistd.h>
// ReSharper disable InconsistentNaming
using System.Runtime.InteropServices;

namespace Hydra.Platform.Linux;

internal static partial class SystemdNativeMethods
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

    [LibraryImport("libc", SetLastError = true)]
    [UnmanagedCallConv(CallConvs = [typeof(System.Runtime.CompilerServices.CallConvCdecl)])]
    internal static partial int dup(int oldFileDescriptor);
}

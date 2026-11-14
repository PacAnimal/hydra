using System.Runtime.InteropServices;
using static Hydra.Platform.Linux.Wayland.FfiNativeMethods;

namespace Hydra.Platform.Linux.Wayland;

// ei_seat_bind_capabilities is variadic (NULL-terminated capability list), and a fixed-arity P/Invoke of a
// variadic function is not portable (arm64 passes variadic arguments differently). libffi's ffi_prep_cif_var
// describes the call as variadic, so the arguments go where the target's variadic convention puts them.
internal static unsafe class EiSeatBinder
{
    private static readonly Lock ResolveLock = new();
    private static nint _function;
    private static nint _typeVoid;
    private static nint _typePointer;
    private static nint _typeSint32;

    internal static void Bind(nint seat, EiCapabilities capabilities)
    {
        Resolve();
        Span<int> caps = stackalloc int[8];
        var count = 0;
        foreach (var capability in Enum.GetValues<EiCapabilities>())
            if (capability != EiCapabilities.None && capabilities.HasFlag(capability)) caps[count++] = (int)capability;

        // seat, the capabilities, then the NULL sentinel
        var total = count + 2;
        var types = stackalloc nint[total];
        var values = stackalloc nint[total];
        var cif = default(FfiCif);
        var seatArg = seat;
        nint terminator = 0;
        ulong result = 0;

        fixed (int* capArgs = caps)
        {
            types[0] = _typePointer;
            values[0] = (nint)(&seatArg);
            for (var i = 0; i < count; i++)
            {
                types[i + 1] = _typeSint32;
                values[i + 1] = (nint)(&capArgs[i]);
            }
            types[total - 1] = _typePointer;
            values[total - 1] = (nint)(&terminator);

            var status = ffi_prep_cif_var(&cif, Abi(), 1, (uint)total, _typeVoid, types);
            if (status != FFI_OK) throw new InvalidOperationException($"ffi_prep_cif_var failed: {status}");
            ffi_call(&cif, _function, &result, values);
        }
    }

    private static int Abi() => RuntimeInformation.ProcessArchitecture switch
    {
        Architecture.X64 => FFI_UNIX64,
        Architecture.Arm64 => FFI_SYSV_AARCH64,
        var other => throw new PlatformNotSupportedException($"no libffi ABI for {other}"),
    };

    private static void Resolve()
    {
        lock (ResolveLock)
        {
            if (_function != 0) return;
            var ffi = NativeLibrary.Load(Libffi);
            _typeVoid = NativeLibrary.GetExport(ffi, ffi_type_void);
            _typePointer = NativeLibrary.GetExport(ffi, ffi_type_pointer);
            _typeSint32 = NativeLibrary.GetExport(ffi, ffi_type_sint32);
            _function = NativeLibrary.GetExport(NativeLibrary.Load(LibeiNativeMethods.Libei), LibeiNativeMethods.ei_seat_bind_capabilities);
        }
    }
}

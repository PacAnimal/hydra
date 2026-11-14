// mirrors <ffi.h>
// ReSharper disable InconsistentNaming
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace Hydra.Platform.Linux.Wayland;

internal static unsafe partial class FfiNativeMethods
{
    internal const string Libffi = "libffi.so.8";

    // -- ffi_status --

    internal const int FFI_OK = 0;

    // -- enum ffi_abi: FFI_DEFAULT_ABI per target --

    internal const int FFI_UNIX64 = 2;          // x86_64
    internal const int FFI_SYSV_AARCH64 = 1;    // aarch64

    // -- exported ffi_type data symbols, resolved with NativeLibrary.GetExport --

    internal const string ffi_type_void = "ffi_type_void";
    internal const string ffi_type_pointer = "ffi_type_pointer";
    internal const string ffi_type_sint32 = "ffi_type_sint32";

    [LibraryImport(Libffi)]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    internal static partial int ffi_prep_cif_var(FfiCif* cif, int abi, uint nfixedargs, uint ntotalargs, nint rtype, nint* atypes);

    [LibraryImport(Libffi)]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    internal static partial void ffi_call(FfiCif* cif, nint fn, void* rvalue, nint* avalue);
}

// ffi_cif: 32 bytes on LP64 Linux (x86_64 and aarch64 add no FFI_EXTRA_CIF_FIELDS; Apple and Windows
// aarch64 do). Size leaves headroom for any such fields; libffi fills it and only reads it back.
[StructLayout(LayoutKind.Explicit, Size = 64)]
internal struct FfiCif
{
    [FieldOffset(0)] internal int abi;
    [FieldOffset(4)] internal uint nargs;
    [FieldOffset(8)] internal nint arg_types;
    [FieldOffset(16)] internal nint rtype;
    [FieldOffset(24)] internal uint bytes;
    [FieldOffset(28)] internal uint flags;
}

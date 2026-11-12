// mirrors <linux/uinput.h> and <unistd.h>
// ReSharper disable InconsistentNaming
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace Tests.Setup;

internal static partial class UinputNativeMethods
{
    private const string Libc = "libc";

    internal const int O_RDWR = 0x2;
    internal const int O_NONBLOCK = 0x800;
    internal const int O_CLOEXEC = 0x80000;

    // _IOW('U', 100..102, int)
    internal const int UI_SET_EVBIT = 0x40045564;
    internal const int UI_SET_KEYBIT = 0x40045565;
    internal const int UI_SET_RELBIT = 0x40045566;
    // _IOW('U', 3, struct uinput_setup): 92 bytes
    internal const int UI_DEV_SETUP = 0x405c5503;
    internal const int UI_DEV_CREATE = 0x5501;
    internal const int UI_DEV_DESTROY = 0x5502;
    // _IOC(_IOC_READ, 'U', 44, 64): the device's sysfs name, "inputN"
    internal const int UI_GET_SYSNAME_64 = unchecked((int)0x8040552C);

    internal const int UINPUT_SETUP_SIZE = 92;
    internal const int UINPUT_MAX_NAME_SIZE = 80;
    internal const ushort BUS_VIRTUAL = 0x06;

    internal const ushort KEY_POWER = 116;
    internal const ushort KEY_LEFTSHIFT = 42;

    [LibraryImport(Libc, EntryPoint = "ioctl")]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    internal static partial int ioctl_int(int fd, int request, int arg);

    [LibraryImport(Libc, EntryPoint = "ioctl")]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    internal static partial int ioctl_buf(int fd, int request, byte[] arg);

    [LibraryImport(Libc, EntryPoint = "write")]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    internal static partial nint write(int fd, byte[] buf, nuint count);
}

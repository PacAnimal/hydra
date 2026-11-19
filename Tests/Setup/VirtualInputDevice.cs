using System.Text;
using Hydra.Platform.Linux;
using static Tests.Setup.UinputNativeMethods;

namespace Tests.Setup;

// A keyboard, mouse or other input device made through /dev/uinput: the kernel creates a real evdev node for it, and
// unplugs it on Unplug or Dispose exactly as it would a USB device. Linux only, and needs /dev/uinput writable.
public sealed class VirtualInputDevice : IDisposable
{
    private const string Uinput = "/dev/uinput";

    private int _fd;

    // the device's evdev node, /dev/input/eventN
    public string Node { get; }

    private VirtualInputDevice(int fd, string node)
    {
        _fd = fd;
        Node = node;
    }

    // uinput is writable, and /dev/input is devtmpfs, where the kernel puts a new device's node (a container's /dev is
    // a plain tmpfs unless run-tests.sh mounts it)
    public static bool Available
    {
        get
        {
            if (!OperatingSystem.IsLinux()) return false;
            var fd = EvdevNativeMethods.Open(Uinput, O_RDWR | O_NONBLOCK | O_CLOEXEC);
            if (fd < 0) return false;
            _ = EvdevNativeMethods.Close(fd);
            // mountinfo: id parent major:minor root mountpoint options ... - fstype source options
            return File.ReadLines("/proc/self/mountinfo")
                .Select(line => line.Split(' '))
                .Any(f => f[4] is "/dev" or "/dev/input" && f.SkipWhile(x => x != "-").Skip(1).FirstOrDefault() == "devtmpfs");
        }
    }

    // reports letter keys and Shift, so evdev's discovery takes it for a keyboard
    public static VirtualInputDevice Keyboard(string name) =>
        Create(name, keys: [EvdevNativeMethods.KEY_A, KEY_LEFTSHIFT], rels: []);

    // every key of a plain pc keyboard (evdev codes 1-127), for typing whole words
    public static VirtualInputDevice FullKeyboard(string name) =>
        Create(name, keys: [.. Enumerable.Range(1, 127).Select(code => (ushort)code)], rels: []);

    // relative X/Y and the left button, so discovery takes it for a pointer
    public static VirtualInputDevice Mouse(string name) =>
        Create(name, keys: [EvdevNativeMethods.BTN_LEFT], rels: [EvdevNativeMethods.REL_X, EvdevNativeMethods.REL_Y]);

    // a three-button mouse with a vertical and a horizontal wheel
    public static VirtualInputDevice WheelMouse(string name) =>
        Create(name, keys: [EvdevNativeMethods.BTN_LEFT, EvdevNativeMethods.BTN_RIGHT, EvdevNativeMethods.BTN_MIDDLE,
                EvdevNativeMethods.BTN_SIDE, EvdevNativeMethods.BTN_EXTRA],
            rels: [EvdevNativeMethods.REL_X, EvdevNativeMethods.REL_Y, EvdevNativeMethods.REL_WHEEL, EvdevNativeMethods.REL_HWHEEL]);

    // a power button: neither keyboard nor pointer
    public static VirtualInputDevice Other(string name) =>
        Create(name, keys: [KEY_POWER], rels: []);

    // a wireless keyboard+mouse receiver's one node: letter keys, the left button and relative X/Y together
    public static VirtualInputDevice Combo(string name) =>
        Create(name, keys: [EvdevNativeMethods.KEY_A, KEY_LEFTSHIFT, EvdevNativeMethods.BTN_LEFT],
            rels: [EvdevNativeMethods.REL_X, EvdevNativeMethods.REL_Y]);

    private static VirtualInputDevice Create(string name, ushort[] keys, ushort[] rels)
    {
        var fd = EvdevNativeMethods.Open(Uinput, O_RDWR | O_NONBLOCK | O_CLOEXEC);
        if (fd < 0) throw new InvalidOperationException($"{Uinput} could not be opened");
        try
        {
            Check(ioctl_int(fd, UI_SET_EVBIT, EvdevNativeMethods.EV_KEY), "UI_SET_EVBIT(EV_KEY)");
            foreach (var key in keys)
                Check(ioctl_int(fd, UI_SET_KEYBIT, key), "UI_SET_KEYBIT");
            if (rels.Length > 0)
            {
                Check(ioctl_int(fd, UI_SET_EVBIT, EvdevNativeMethods.EV_REL), "UI_SET_EVBIT(EV_REL)");
                foreach (var rel in rels)
                    Check(ioctl_int(fd, UI_SET_RELBIT, rel), "UI_SET_RELBIT");
            }

            // struct uinput_setup: input_id { bustype, vendor, product, version }, name[80], ff_effects_max
            var setup = new byte[UINPUT_SETUP_SIZE];
            BitConverter.TryWriteBytes(setup.AsSpan(0), BUS_VIRTUAL);
            Encoding.ASCII.GetBytes(name, 0, Math.Min(name.Length, UINPUT_MAX_NAME_SIZE - 1), setup, 8);
            Check(ioctl_buf(fd, UI_DEV_SETUP, setup), "UI_DEV_SETUP");
            Check(ioctl_int(fd, UI_DEV_CREATE, 0), "UI_DEV_CREATE");

            var sysname = new byte[64];
            Check(ioctl_buf(fd, UI_GET_SYSNAME_64, sysname), "UI_GET_SYSNAME");
            var input = Encoding.ASCII.GetString(sysname).TrimEnd('\0');
            var node = Path.Combine("/dev/input", WaitFor(() => EventName(input)));
            // the node is the kernel's at once, but udev sets who may open it after that
            WaitFor(() => Readable(node) ? node : null);
            return new VirtualInputDevice(fd, node);
        }
        catch
        {
            _ = EvdevNativeMethods.Close(fd);
            throw;
        }
    }

    // the eventN the kernel gave the device, once its evdev handler has bound
    private static string? EventName(string input)
    {
        var dir = $"/sys/devices/virtual/input/{input}";
        return Directory.Exists(dir) ? Directory.GetDirectories(dir, "event*").Select(Path.GetFileName).FirstOrDefault() : null;
    }

    private static bool Readable(string node)
    {
        var fd = EvdevNativeMethods.Open(node, EvdevNativeMethods.O_RDONLY | EvdevNativeMethods.O_NONBLOCK | EvdevNativeMethods.O_CLOEXEC);
        if (fd < 0) return false;
        _ = EvdevNativeMethods.Close(fd);
        return true;
    }

    private static string WaitFor(Func<string?> found)
    {
        var deadline = DateTime.UtcNow + TestBound.Bound;
        while (true)
        {
            if (found() is { } value) return value;
            if (DateTime.UtcNow > deadline) throw new TimeoutException("The virtual device's node never appeared");
            Thread.Sleep(10);
        }
    }

    private static void Check(int result, string what)
    {
        if (result < 0) throw new InvalidOperationException($"{what} failed");
    }

    public void Key(ushort code, bool down)
    {
        Emit(EvdevNativeMethods.EV_KEY, code, down ? 1 : 0);
        Emit(EvdevNativeMethods.EV_SYN, 0, 0);
    }

    // a button is a key to evdev (BTN_LEFT and up)
    public void Button(ushort code, bool down) => Key(code, down);

    // one relative motion report
    public void Move(int dx, int dy)
    {
        if (dx != 0) Emit(EvdevNativeMethods.EV_REL, EvdevNativeMethods.REL_X, dx);
        if (dy != 0) Emit(EvdevNativeMethods.EV_REL, EvdevNativeMethods.REL_Y, dy);
        Emit(EvdevNativeMethods.EV_SYN, 0, 0);
    }

    // wheel notches, positive away from the user (up), as REL_WHEEL counts them
    public void Wheel(int notches)
    {
        Emit(EvdevNativeMethods.EV_REL, EvdevNativeMethods.REL_WHEEL, notches);
        Emit(EvdevNativeMethods.EV_SYN, 0, 0);
    }

    // wheel notches, positive towards the user (down), so REL_WHEEL gets the opposite sign
    public void Scroll(int notches) => Wheel(-notches);

    private void Emit(ushort type, ushort code, int value)
    {
        // struct input_event: timeval (two longs, left zero for the kernel to stamp), type, code, value
        var ev = new byte[24];
        BitConverter.TryWriteBytes(ev.AsSpan(16), type);
        BitConverter.TryWriteBytes(ev.AsSpan(18), code);
        BitConverter.TryWriteBytes(ev.AsSpan(20), value);
        if (write(_fd, ev, (nuint)ev.Length) < 0)
            throw new InvalidOperationException("Writing to the virtual device failed");
    }

    // the kernel removes the node and hangs up every reader, as when a USB device is pulled
    public void Unplug()
    {
        if (_fd < 0) return;
        _ = ioctl_int(_fd, UI_DEV_DESTROY, 0);
        _ = EvdevNativeMethods.Close(_fd);
        _fd = -1;
    }

    public void Dispose() => Unplug();
}

using System.Text;
using static Hydra.Platform.Linux.Wayland.WaylandProtocols;

namespace Hydra.Platform.Linux.Wayland;

// a position for zwlr_virtual_pointer_v1.motion_absolute: X of XExtent across, Y of YExtent down
internal readonly record struct AbsoluteMotion(uint X, uint Y, uint XExtent, uint YExtent);

// injects input through the wlroots virtual pointer and virtual keyboard protocols (Hyprland, Sway), for a compositor
// whose portal has no RemoteDesktop. The client is not owned. Pointer requests are grouped by Frame; keyboard requests
// need a keymap first, and the compositor does not derive modifiers from keys, so the caller sends Modifiers itself.
internal sealed class VirtualInput : IDisposable
{
    internal const uint KeymapFormatXkbV1 = 1;
    internal const uint Released = 0;
    internal const uint Pressed = 1;
    internal const uint AxisVertical = 0;
    internal const uint AxisHorizontal = 1;
    internal const uint AxisSourceWheel = 0;

    private const uint SeatVersion = 1;
    private const uint PointerManagerVersion = 2;
    private const uint KeyboardManagerVersion = 1;

    private readonly WaylandClient _client;
    private readonly nint _seat;
    private readonly nint _pointerManager;
    private readonly nint _keyboardManager;
    private readonly nint _pointer;
    private readonly nint _keyboard;
    private volatile bool _hasKeymap;
    private int _disposed;

    // throws when the compositor offers no seat or lacks either protocol (see IsSupported)
    internal VirtualInput(WaylandClient client)
    {
        _client = client;
        var seat = client.Find(Seat) ?? throw new InvalidOperationException("the compositor has no wl_seat");
        var pointerManager = client.Find(VirtualPointerManager)
            ?? throw new InvalidOperationException($"the compositor has no {VirtualPointerManager}");
        var keyboardManager = client.Find(VirtualKeyboardManager)
            ?? throw new InvalidOperationException($"the compositor has no {VirtualKeyboardManager}");
        try
        {
            _seat = client.Bind(seat, Seat, SeatVersion);
            _pointerManager = client.Bind(pointerManager, VirtualPointerManager, PointerManagerVersion);
            _keyboardManager = client.Bind(keyboardManager, VirtualKeyboardManager, KeyboardManagerVersion);
            _pointer = client.Create(_pointerManager, PointerManagerCreate, VirtualPointer, null,
                WaylandArgs.Object(_seat), WaylandArgs.NewId());
            _keyboard = client.Create(_keyboardManager, KeyboardManagerCreate, VirtualKeyboard, null,
                WaylandArgs.Object(_seat), WaylandArgs.NewId());
            client.Flush();
        }
        catch
        {
            Dispose();
            throw;
        }
    }

    // whether the compositor offers what the constructor needs
    internal static bool IsSupported(WaylandClient client) =>
        client.Find(Seat) != null && client.Find(VirtualPointerManager) != null && client.Find(VirtualKeyboardManager) != null;

    // -- keyboard --

    // hands the compositor an XKB keymap (text, as xkb_keymap_get_as_string gives it) through an unlinked file;
    // libwayland dups the fd when it marshals, so ours closes straight after
    internal void UploadKeymap(string keymapText)
    {
        var bytes = KeymapBytes(keymapText);
        var path = KeymapPath();
        var options = new FileStreamOptions { Mode = FileMode.CreateNew, Access = FileAccess.ReadWrite };
        if (!OperatingSystem.IsWindows()) options.UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite;
        using var file = new FileStream(path, options);
        try
        {
            file.Write(bytes);
            file.Flush();
        }
        finally
        {
            File.Delete(path);
        }
        var fd = (int)file.SafeFileHandle.DangerousGetHandle();
        _client.Marshal(_keyboard, KeyboardKeymap,
            WaylandArgs.UInt(KeymapFormatXkbV1), WaylandArgs.Fd(fd), WaylandArgs.UInt((uint)bytes.Length));
        _client.Flush();
        _hasKeymap = true;
    }

    // an evdev key code (KEY_*), not an XKB keycode
    internal void Key(uint evdevKey, bool pressed)
    {
        RequireKeymap();
        _client.Marshal(_keyboard, KeyboardKey, WaylandArgs.UInt(Now()), WaylandArgs.UInt(evdevKey), WaylandArgs.UInt(pressed ? Pressed : Released));
    }

    // XKB modifier masks of the uploaded keymap and its layout index
    internal void Modifiers(uint depressed, uint latched, uint locked, uint group)
    {
        RequireKeymap();
        _client.Marshal(_keyboard, KeyboardModifiers,
            WaylandArgs.UInt(depressed), WaylandArgs.UInt(latched), WaylandArgs.UInt(locked), WaylandArgs.UInt(group));
    }

    // -- pointer --

    internal void Motion(double dx, double dy) =>
        _client.Marshal(_pointer, PointerMotion, WaylandArgs.UInt(Now()), WaylandArgs.Fixed(dx), WaylandArgs.Fixed(dy));

    // x of xExtent across the bounding box of all outputs (see Normalise), y likewise
    internal void MotionAbsolute(uint x, uint y, uint xExtent, uint yExtent) =>
        _client.Marshal(_pointer, PointerMotionAbsolute,
            WaylandArgs.UInt(Now()), WaylandArgs.UInt(x), WaylandArgs.UInt(y), WaylandArgs.UInt(xExtent), WaylandArgs.UInt(yExtent));

    internal void MotionAbsolute(AbsoluteMotion motion) => MotionAbsolute(motion.X, motion.Y, motion.XExtent, motion.YExtent);

    // an evdev button code (BTN_LEFT = 0x110, ...)
    internal void Button(uint evdevButton, bool pressed) =>
        _client.Marshal(_pointer, PointerButton, WaylandArgs.UInt(Now()), WaylandArgs.UInt(evdevButton), WaylandArgs.UInt(pressed ? Pressed : Released));

    // value in wl_pointer axis units (15 a wheel notch on most compositors), discrete in notches
    internal void AxisDiscrete(uint axis, double value, int discrete) =>
        _client.Marshal(_pointer, PointerAxisDiscrete, WaylandArgs.UInt(Now()), WaylandArgs.UInt(axis), WaylandArgs.Fixed(value), WaylandArgs.Int(discrete));

    internal void AxisSource(uint source) => _client.Marshal(_pointer, PointerAxisSource, WaylandArgs.UInt(source));

    // ends a group of pointer requests and sends them
    internal void Frame()
    {
        _client.Marshal(_pointer, PointerFrame);
        _client.Flush();
    }

    // a layout position as motion_absolute takes it: relative to the bounding box of every output, clamped inside
    // it. Null without outputs.
    internal static AbsoluteMotion? Normalise(int x, int y, IReadOnlyList<DetectedScreen> outputs)
    {
        if (outputs.Count == 0) return null;
        var left = outputs.Min(s => s.X);
        var top = outputs.Min(s => s.Y);
        var right = outputs.Max(s => (long)s.X + s.Width);
        var bottom = outputs.Max(s => (long)s.Y + s.Height);
        var width = right - left;
        var height = bottom - top;
        if (width <= 0 || height <= 0) return null;
        var nx = Math.Clamp((long)x - left, 0, width - 1);
        var ny = Math.Clamp((long)y - top, 0, height - 1);
        return new AbsoluteMotion((uint)nx, (uint)ny, (uint)width, (uint)height);
    }

    // the keymap as the compositor maps it: UTF-8 with a terminating NUL, which the size counts
    internal static byte[] KeymapBytes(string keymapText)
    {
        var bytes = new byte[Encoding.UTF8.GetByteCount(keymapText) + 1];
        Encoding.UTF8.GetBytes(keymapText, bytes);
        return bytes;
    }

    // milliseconds on a monotonic clock, wrapping as the protocol's uint does
    private static uint Now() => unchecked((uint)Environment.TickCount64);

    private static string KeymapPath()
    {
        var runtime = Environment.GetEnvironmentVariable("XDG_RUNTIME_DIR");
        var dir = !string.IsNullOrEmpty(runtime) && Directory.Exists(runtime) ? runtime : Path.GetTempPath();
        return Path.Combine(dir, $"hydra-keymap-{Environment.ProcessId}-{Guid.NewGuid():N}");
    }

    private void RequireKeymap()
    {
        // the compositor ends the connection for a key or modifiers before a keymap
        if (!_hasKeymap) throw new InvalidOperationException("no keymap uploaded");
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        if (_keyboard != 0) _client.Destroy(_keyboard, KeyboardDestroy);
        if (_pointer != 0) _client.Destroy(_pointer, PointerDestroy);
        if (_pointerManager != 0) _client.Destroy(_pointerManager, PointerManagerDestroy);
        if (_keyboardManager != 0) _client.Destroy(_keyboardManager);
        if (_seat != 0) _client.Destroy(_seat);
        _client.Flush();
    }
}

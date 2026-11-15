using System.Globalization;
using System.Text.Json;
using Hydra.Platform.Linux;
using Hydra.Platform.Linux.Wayland;
using Microsoft.Extensions.Logging;
using Microsoft.Win32.SafeHandles;
using Tests.Setup;
using Tmds.DBus.Protocol;

namespace Tests.Desktops;

// the physical user's mouse and keyboard on a test desktop: xtest keys (by physical key) and xdotool mouse on x11, the
// compositor's own eis socket on gnome
// and kde, uinput devices on bare evdev and on hyprland (which reads them through libinput). On x11 and eis a call returns once the display server or compositor has
// processed its input (eis by a ping round trip after each frame), so steps never queue up behind each other;
// on uinput, once its input has left this process.
internal sealed class UserHands : IAsyncDisposable
{
    private const int MaxStep = 10;
    // hydra reads bare evdev itself and needs no barrier approach: fewer, larger steps, in batches of which two fit the
    // kernel's evdev buffer (three events a step, 64 in all)
    private const int EvdevMaxStep = 50;
    private const int EvdevBatch = 10;
    private const int DiscretePerNotch = 120;

    // evdev codes EvdevNativeMethods does not declare
    private const int KeySpace = 57;

    // evdev numbers the letter keys along the qwerty rows: q is 16, a is 30, z is 44
    private static readonly Dictionary<char, int> Letters = new[] { ("qwertyuiop", 16), ("asdfghjkl", 30), ("zxcvbnm", 44) }
        .SelectMany(row => row.Item1.Select((letter, i) => KeyValuePair.Create(letter, row.Item2 + i)))
        .ToDictionary();

    private readonly IBackend _backend;
    private readonly int _maxStep;
    // begins a batch of moves and returns what to await once it is written; null: no pacing
    private readonly Func<Func<Task>>? _pace;

    private UserHands(IBackend backend, int maxStep, Func<Func<Task>>? pace)
    {
        _backend = backend;
        _maxStep = maxStep;
        _pace = pace;
    }

    // pace: on bare evdev and hyprland, called before each batch of moves; the task it returns completes once the batch was read
    public static async Task<UserHands> Attach(Desktop desktop, ILogger log, Func<Func<Task>>? pace = null)
    {
        IBackend backend = desktop.Kind switch
        {
            DesktopKind.Openbox or DesktopKind.KdeX11 => new X11Backend(desktop),
            DesktopKind.Gnome => await EisBackend.Mutter(desktop, log),
            DesktopKind.Kde => await EisBackend.KWin(desktop, log),
            DesktopKind.Evdev => new UinputBackend(),
            DesktopKind.Hyprland => await UinputBackend.SeenBy(desktop),
            _ => throw new ArgumentOutOfRangeException(nameof(desktop), desktop.Kind, "no hands for this desktop"),
        };
        return desktop.Kind switch
        {
            DesktopKind.Evdev => new UserHands(backend, EvdevMaxStep, pace),
            // hyprland's reader has the same kernel buffer, so its uinput writes are paced in the same batches
            DesktopKind.Hyprland => new UserHands(backend, MaxStep, pace),
            _ => new UserHands(backend, MaxStep, null),
        };
    }

    // relative motion in steps of at most MaxStep px, so a barrier sees the pointer approach; paced batches on uinput
    public async Task MoveBy(int dx, int dy)
    {
        var steps = Math.Max(Steps(dx), Steps(dy));
        int x = 0, y = 0;
        for (var first = 1; first <= steps; first += EvdevBatch)
        {
            var last = _pace == null ? steps : Math.Min(steps, first + EvdevBatch - 1);
            var read = _pace?.Invoke();
            for (var i = first; i <= last; i++)
            {
                var nx = dx * i / steps;
                var ny = dy * i / steps;
                await _backend.Move(nx - x, ny - y);
                x = nx;
                y = ny;
            }
            if (read == null)
                break;
            await read();
        }
    }

    private int Steps(int d) => (Math.Abs(d) + _maxStep - 1) / _maxStep;

    public Task Button(int evdevButton, bool pressed) => _backend.Button(evdevButton, pressed);

    public Task Key(int evdevKey, bool pressed) => _backend.Key(evdevKey, pressed);

    public async Task Tap(params int[] evdevKeys)
    {
        foreach (var key in evdevKeys) await Key(key, true);
        for (var i = evdevKeys.Length - 1; i >= 0; i--) await Key(evdevKeys[i], false);
    }

    // lowercase letters, digits and space only
    public async Task Type(string ascii)
    {
        foreach (var c in ascii) await Tap(KeyFor(c));
    }

    // physical keys, tapped one after another: what they type is up to the desktop's layout
    public async Task TypeKeys(params int[] evdevKeys)
    {
        foreach (var key in evdevKeys) await Tap(key);
    }

    // the modifiers held in order around a tap of key, released in reverse
    public async Task Chord(int[] modifiers, int key)
    {
        foreach (var modifier in modifiers) await Key(modifier, true);
        await Tap(key);
        for (var i = modifiers.Length - 1; i >= 0; i--) await Key(modifiers[i], false);
    }

    private static int KeyFor(char c) => c switch
    {
        ' ' => KeySpace,
        '0' => 11,
        >= '1' and <= '9' => 2 + (c - '1'),
        _ when Letters.TryGetValue(c, out var code) => code,
        _ => throw new ArgumentException($"UserHands cannot type '{c}'", nameof(c)),
    };

    // positive is down, towards the user
    public Task Scroll(int notches) => notches == 0 ? Task.CompletedTask : _backend.Scroll(notches);

    public ValueTask DisposeAsync() => _backend.DisposeAsync();

    private interface IBackend : IAsyncDisposable
    {
        Task Move(int dx, int dy);
        Task Button(int evdevButton, bool pressed);
        Task Key(int evdevKey, bool pressed);
        Task Scroll(int notches);
    }

    // an x11 server's hands: keys through xtest by physical key, the mouse through xdotool
    private sealed class X11Backend(Desktop desktop) : IBackend
    {
        public Task Move(int dx, int dy) =>
            Run("xdotool", "mousemove_relative", "--", dx.ToString(CultureInfo.InvariantCulture), dy.ToString(CultureInfo.InvariantCulture));

        public Task Button(int evdevButton, bool pressed)
        {
            var button = evdevButton switch
            {
                EvdevNativeMethods.BTN_LEFT => "1",
                EvdevNativeMethods.BTN_MIDDLE => "2",
                EvdevNativeMethods.BTN_RIGHT => "3",
                _ => throw new ArgumentException($"no x11 button for evdev button {evdevButton}", nameof(evdevButton)),
            };
            return Run("xdotool", pressed ? "mousedown" : "mouseup", button);
        }

        // xtest on the key's x keycode, evdev's plus 8 as xvfb's evdev rules number them: the physical key, whatever the
        // layout. A python3 one-shot, as xdotool would pick a key by keysym; its XSync returns once the server has the event
        public Task Key(int evdevKey, bool pressed) =>
            Run("python3", "-c", FakeKey, (evdevKey + 8).ToString(CultureInfo.InvariantCulture), pressed ? "1" : "0");

        public Task Scroll(int notches) =>
            Run("xdotool", "click", "--repeat", Math.Abs(notches).ToString(CultureInfo.InvariantCulture), notches > 0 ? "5" : "4");

        private const string FakeKey = """
            import ctypes, sys
            x = ctypes.CDLL("libX11.so.6")
            t = ctypes.CDLL("libXtst.so.6")
            x.XOpenDisplay.restype = ctypes.c_void_p
            x.XFlush.argtypes = [ctypes.c_void_p]
            x.XSync.argtypes = [ctypes.c_void_p, ctypes.c_int]
            x.XCloseDisplay.argtypes = [ctypes.c_void_p]
            t.XTestFakeKeyEvent.argtypes = [ctypes.c_void_p, ctypes.c_uint, ctypes.c_int, ctypes.c_ulong]
            d = x.XOpenDisplay(None)
            if not d:
                sys.exit("cannot open display")
            if not t.XTestFakeKeyEvent(d, int(sys.argv[1]), int(sys.argv[2]), 0):
                sys.exit("XTestFakeKeyEvent failed")
            x.XFlush(d)
            x.XSync(d, 0)
            x.XCloseDisplay(d)
            """;

        private async Task Run(string file, params string[] args)
        {
            var info = desktop.Process(file, args);
            info.UseShellExecute = false;
            info.RedirectStandardError = true;
            using var process = System.Diagnostics.Process.Start(info)
                ?? throw new InvalidOperationException($"{file} did not start");
            process.StandardInput.Close();
            var stderr = process.StandardError.ReadToEndAsync();
            var what = file == "python3" ? $"xtest key {args[^2]} {args[^1]}" : $"{file} {string.Join(' ', args)}";
            using var bound = new CancellationTokenSource(TestBound.Bound);
            try
            {
                await process.WaitForExitAsync(bound.Token);
            }
            catch (OperationCanceledException)
            {
                process.Kill(entireProcessTree: true);
                throw new TimeoutException($"{what} did not exit within {TestBound.Bound}");
            }
            var error = await stderr;
            if (process.ExitCode != 0)
                throw new InvalidOperationException($"{what} exited {process.ExitCode}: {error.Trim()}");
        }

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    // a call returns once the compositor has processed its input, as xdotool's does on x11
    private sealed class EisBackend : IBackend
    {
        private const string MutterName = "org.gnome.Mutter.RemoteDesktop";
        private const string MutterPath = "/org/gnome/Mutter/RemoteDesktop";
        private const string MutterSession = "org.gnome.Mutter.RemoteDesktop.Session";
        private const string KWinName = "org.kde.KWin";
        private const string KWinPath = "/org/kde/KWin/EIS/RemoteDesktop";
        private const string KWinInterface = "org.kde.KWin.EIS.RemoteDesktop";
        // hydratest-portal.py, the portal backend desktop.sh runs on gnome and kde
        private const string TestPortalName = "org.freedesktop.impl.portal.desktop.hydratest";
        private const string TestPortalPath = "/org/hydratest/Control";
        private const string TestPortalControl = "org.hydratest.Control";

        private readonly DBusConnection _bus;
        private readonly SafeFileHandle _fd;
        private readonly EiContext _ei;
        private readonly Func<Task> _close;

        private EisBackend(DBusConnection bus, SafeFileHandle fd, EiContext ei, Func<Task> close)
        {
            _bus = bus;
            _fd = fd;
            _ei = ei;
            _close = close;
        }

        internal static async Task<EisBackend> Mutter(Desktop desktop, ILogger log)
        {
            var bus = await Connect(desktop);
            try
            {
                var session = await bus.CallMethodAsync(Call(bus, MutterName, MutterPath, MutterName, "CreateSession"),
                    (message, _) => message.GetBodyReader().ReadObjectPathAsString()).WaitAsync(TestBound.Bound);
                await bus.CallMethodAsync(Call(bus, MutterName, session, MutterSession, "Start")).WaitAsync(TestBound.Bound);
                var fd = await bus.CallMethodAsync(Call(bus, MutterName, session, MutterSession, "ConnectToEIS", "a{sv}",
                        (ref MessageWriter writer) => writer.WriteDictionary(new Dictionary<string, VariantValue>())),
                    (message, _) => message.GetBodyReader().ReadHandle<SafeFileHandle>()).WaitAsync(TestBound.Bound);
                // stopping the session is best effort; mutter also ends it when the bus connection goes
                return await Open(bus, fd, log, () => bus.CallMethodAsync(Call(bus, MutterName, session, MutterSession, "Stop")));
            }
            catch
            {
                bus.Dispose();
                throw;
            }
        }

        internal static async Task<EisBackend> KWin(Desktop desktop, ILogger log)
        {
            var bus = await Connect(desktop);
            try
            {
                // capabilities 3: keyboard and pointer
                var reply = await bus.CallMethodAsync(Call(bus, KWinName, KWinPath, KWinInterface, "connectToEIS", "i",
                        (ref MessageWriter writer) => writer.WriteInt32(3)),
                    (message, _) =>
                    {
                        var reader = message.GetBodyReader();
                        var handle = reader.ReadHandle<SafeFileHandle>();
                        return new KWinReply(handle, reader.ReadInt32());
                    }).WaitAsync(TestBound.Bound);
                return await Open(bus, reply.Fd, log, () => bus.CallMethodAsync(Call(bus, KWinName, KWinPath, KWinInterface, "disconnect", "i",
                    (ref MessageWriter writer) => writer.WriteInt32(reply.Cookie))));
            }
            catch
            {
                bus.Dispose();
                throw;
            }
        }

        private static async Task<DBusConnection> Connect(Desktop desktop)
        {
            var bus = new DBusConnection(desktop.Env["DBUS_SESSION_BUS_ADDRESS"]);
            try
            {
                await bus.ConnectAsync().AsTask().WaitAsync(TestBound.Bound);
                return bus;
            }
            catch
            {
                bus.Dispose();
                throw;
            }
        }

        private static MessageBuffer Call(DBusConnection bus, string destination, string path, string interfaceName, string member,
            string? signature = null, WriteBody? body = null)
        {
            var writer = bus.GetMessageWriter();
            try
            {
                writer.WriteMethodCallHeader(destination, path, interfaceName, member, signature);
                body?.Invoke(ref writer);
                return writer.CreateMessage();
            }
            finally
            {
                writer.Dispose();
            }
        }

        // a sender on the eis socket, once the compositor has resumed a pointer and a keyboard on it
        private static async Task<EisBackend> Open(DBusConnection bus, SafeFileHandle fd, ILogger log, Func<Task> close)
        {
            EiContext? ei = null;
            try
            {
                ei = EiContext.Sender(fd, "hydra-test-hands", log);
                var ready = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                var context = ei;
                void Check()
                {
                    if (context.Pointer != null && context.Keyboard != null) ready.TrySetResult();
                }
                void Resumed(EiDevice _) => Check();
                ei.DeviceResumed += Resumed;
                try
                {
                    Check();
                    await ready.Task.WaitAsync(TestBound.Bound);
                }
                finally
                {
                    ei.DeviceResumed -= Resumed;
                }
                // no StartEmulating here: each send starts only the device it uses, and emulating mutter's shared absolute
                // pointer as well keeps the hands' keys and clicks out of an input capture
                return new EisBackend(bus, fd, ei, close);
            }
            catch
            {
                ei?.Dispose();
                fd.Dispose();
                throw;
            }
        }

        public Task Move(int dx, int dy) => Send(_ei.SendPointerMotion(dx, dy), "pointer motion");

        public Task Button(int evdevButton, bool pressed) => Send(_ei.SendButton((uint)evdevButton, pressed), "button");

        // the test portal sees no key the compositor handles, so each is told to it too: it fires a bound shortcut's
        // Activated and Deactivated as GNOME's and KDE's own portals would, and the call returns once it has
        public async Task Key(int evdevKey, bool pressed)
        {
            await Send(_ei.SendKeyboardKey((uint)evdevKey, pressed), "key");
            await _bus.CallMethodAsync(Call(_bus, TestPortalName, TestPortalPath, TestPortalControl, "Key", "ub",
                (ref MessageWriter writer) =>
                {
                    writer.WriteUInt32((uint)evdevKey);
                    writer.WriteBool(pressed);
                })).WaitAsync(TestBound.Bound);
        }

        public Task Scroll(int notches) => Send(_ei.SendScrollDiscrete(0, notches * DiscretePerNotch), "scroll");

        private Task Send(bool sent, string what)
        {
            if (sent && _ei.SendFrame()) return _ei.Ping().WaitAsync(TestBound.Bound);
            throw new InvalidOperationException(sent
                ? $"the eis sender could not frame {what}"
                : $"the eis sender has no device for {what}");
        }

        public async ValueTask DisposeAsync()
        {
            _ei.StopEmulating();
            _ei.Dispose();
            _fd.Dispose();
            try
            {
                await _close().WaitAsync(TestBound.Bound);
            }
            catch (DBusExceptionBase)
            {
                // the compositor may have dropped the session already
            }
            finally
            {
                _bus.Dispose();
            }
        }

        private sealed record KWinReply(SafeFileHandle Fd, int Cookie);
    }

    private sealed class UinputBackend : IBackend
    {
        private const string KeyboardName = "hydra-test-hands-keyboard";
        private const string MouseName = "hydra-test-hands-mouse";

        private readonly VirtualInputDevice _keyboard;
        private readonly VirtualInputDevice _mouse;

        // devices hyprland reads itself, through libinput; it takes them on hotplug, so they are usable once it lists both
        internal static async Task<UinputBackend> SeenBy(Desktop desktop)
        {
            var backend = new UinputBackend();
            try
            {
                var bound = System.Diagnostics.Stopwatch.StartNew();
                while (!await Lists(desktop))
                {
                    if (bound.Elapsed >= TestBound.Bound)
                        throw new TimeoutException($"hyprland did not list {KeyboardName} and {MouseName} within {TestBound.Bound}");
                }
                return backend;
            }
            catch
            {
                await backend.DisposeAsync();
                throw;
            }
        }

        // whether hyprctl's device list has both, as a keyboard and a mouse
        private static async Task<bool> Lists(Desktop desktop)
        {
            using var process = System.Diagnostics.Process.Start(desktop.Process("hyprctl", "-j", "devices"))
                ?? throw new InvalidOperationException("hyprctl did not start");
            process.StandardInput.Close();
            var stdout = process.StandardOutput.ReadToEndAsync();
            var stderr = process.StandardError.ReadToEndAsync();
            using var cts = new CancellationTokenSource(TestBound.Bound);
            try
            {
                await process.WaitForExitAsync(cts.Token);
            }
            catch (OperationCanceledException)
            {
                process.Kill(entireProcessTree: true);
                throw new TimeoutException($"hyprctl -j devices did not exit within {TestBound.Bound}");
            }
            var json = await stdout;
            if (process.ExitCode != 0)
                throw new InvalidOperationException($"hyprctl -j devices exited {process.ExitCode}: {(await stderr).Trim()}");
            using var doc = JsonDocument.Parse(json);
            return Named(doc.RootElement, "keyboards", KeyboardName) && Named(doc.RootElement, "mice", MouseName);
        }

        private static bool Named(JsonElement devices, string list, string name) =>
            devices.TryGetProperty(list, out var entries) && entries.ValueKind == JsonValueKind.Array &&
            entries.EnumerateArray().Any(d => d.TryGetProperty("name", out var n) && n.GetString() == name);

        public UinputBackend()
        {
            _keyboard = VirtualInputDevice.FullKeyboard(KeyboardName);
            try
            {
                _mouse = VirtualInputDevice.WheelMouse(MouseName);
            }
            catch
            {
                _keyboard.Dispose();
                throw;
            }
        }

        public Task Move(int dx, int dy)
        {
            _mouse.Move(dx, dy);
            return Task.CompletedTask;
        }

        public Task Button(int evdevButton, bool pressed)
        {
            _mouse.Button((ushort)evdevButton, pressed);
            return Task.CompletedTask;
        }

        public Task Key(int evdevKey, bool pressed)
        {
            _keyboard.Key((ushort)evdevKey, pressed);
            return Task.CompletedTask;
        }

        public Task Scroll(int notches)
        {
            _mouse.Scroll(notches);
            return Task.CompletedTask;
        }

        public ValueTask DisposeAsync()
        {
            _mouse.Dispose();
            _keyboard.Dispose();
            return ValueTask.CompletedTask;
        }
    }
}

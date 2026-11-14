using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using Microsoft.Extensions.Logging;
using static Hydra.Platform.Linux.Wayland.WaylandClientNativeMethods;

namespace Hydra.Platform.Linux.Wayland;

internal sealed record WaylandGlobal(uint Name, string Interface, uint Version);

// an event's arguments as libwayland decoded them, valid only during the callback
internal readonly ref struct WaylandEventArgs
{
    private readonly ReadOnlySpan<WlArgument> _args;

    internal WaylandEventArgs(ReadOnlySpan<WlArgument> args)
    {
        _args = args;
    }

    internal int Int(int index) => _args[index].i;
    internal uint UInt(int index) => _args[index].u;
    // an fd the event hands over; the handler owns it and closes it
    internal int Fd(int index) => _args[index].h;
    internal string? String(int index) => Marshal.PtrToStringUTF8(_args[index].s);
}

internal delegate void WaylandEventHandler(uint opcode, WaylandEventArgs args);

// wl_argument packing and wl_fixed_t (24.8) conversion
internal static class WaylandArgs
{
    internal static WlArgument Int(int value) => new() { i = value };
    internal static WlArgument UInt(uint value) => new() { u = value };
    internal static WlArgument Fixed(double value) => new() { f = ToFixed(value) };
    internal static WlArgument Object(nint proxy) => new() { o = proxy };
    internal static WlArgument Fd(int fd) => new() { h = fd };
    internal static WlArgument String(nint utf8) => new() { s = utf8 };
    // placeholder libwayland fills with the proxy it creates
    internal static WlArgument NewId() => new() { n = 0 };

    internal static int ToFixed(double value)
    {
        var scaled = Math.Round(value * 256.0);
        if (double.IsNaN(scaled)) return 0;
        return (int)Math.Clamp(scaled, int.MinValue, int.MaxValue);
    }

    internal static double FromFixed(int value) => value / 256.0;

    // how many wl_arguments a message signature describes: one per type letter, never the "since" digits or "?"
    internal static int Count(string signature)
    {
        var count = 0;
        foreach (var c in signature)
            if (char.IsAsciiLetter(c)) count++;
        return count;
    }
}

// one libwayland-client connection. libwayland is not thread-safe for us, so every wl_* call is made under _lock.
// A dispatch thread polls the display fd outside it and reads, dispatches and flushes inside it; event handlers run
// on that thread under the lock and must not block, and anything they Defer runs after it is released.
internal sealed unsafe class WaylandClient : IDisposable
{
    private const int PollTimeoutMs = 100;
    private const uint RegistryGlobal = 0;
    private const uint RegistryGlobalRemove = 1;
    private const uint DisplayGetRegistry = 1;
    private const uint RegistryBind = 0;
    private const uint SeatVersion = 5;
    private const uint SeatCapabilities = 0;
    private const uint SeatCapabilityKeyboard = 1 << 1;
    private const uint SeatGetKeyboard = 1;
    private const uint SeatRelease = 3;
    private const uint KeyboardKeymap = 0;
    private const uint KeyboardRelease = 0;
    private const uint KeymapFormatXkbV1 = 1;

    private readonly Lock _lock = new();
    private readonly ILogger _log;
    private readonly Dictionary<nint, WaylandEventHandler> _handlers = [];
    private readonly Dictionary<uint, WaylandGlobal> _globals = [];
    private readonly List<Action> _deferred = [];
    private GCHandle _self;
    private readonly nint _registryInterface;
    private readonly nint _keyboardInterface;
    private Thread? _thread;
    private nint _display;
    private nint _registry;
    private volatile bool _stopping;
    private int _lost;

    public event Action<WaylandGlobal>? GlobalAdded;
    public event Action<WaylandGlobal>? GlobalRemoved;
    // the connection failed or the compositor closed it; raised once
    public event Action? Disconnected;

    private WaylandClient(nint display, ILogger log)
    {
        _display = display;
        _log = log;
        _self = GCHandle.Alloc(this);
        var lib = NativeLibrary.Load(Libwayland);
        _registryInterface = NativeLibrary.GetExport(lib, wl_registry_interface);
        _keyboardInterface = NativeLibrary.GetExport(lib, wl_keyboard_interface);
    }

    internal WaylandProtocols Protocols { get; } = WaylandProtocols.Shared;

    internal bool IsDisconnected => Volatile.Read(ref _lost) != 0;

    internal IReadOnlyList<WaylandGlobal> Globals
    {
        get
        {
            lock (_lock) return [.. _globals.Values.OrderBy(g => g.Name)];
        }
    }

    // connects to $WAYLAND_DISPLAY and reads the globals; throws when there is no compositor
    internal static WaylandClient Connect(ILogger log)
    {
        var display = wl_display_connect(null);
        if (display == 0) throw new InvalidOperationException("cannot connect to the Wayland display");
        var client = new WaylandClient(display, log);
        try
        {
            client.Start();
            return client;
        }
        catch
        {
            client.Dispose();
            throw;
        }
    }

    private void Start()
    {
        lock (_lock)
        {
            var args = stackalloc WlArgument[1];
            args[0] = WaylandArgs.NewId();
            _registry = wl_proxy_marshal_array_flags(_display, DisplayGetRegistry, _registryInterface, wl_proxy_get_version(_display), 0, args);
            if (_registry == 0) throw new InvalidOperationException("wl_display.get_registry failed");
            Listen(_registry, OnRegistry);
        }
        if (!Roundtrip()) throw new InvalidOperationException("the Wayland display did not answer");
        _thread = new Thread(DispatchLoop) { Name = "HydraWaylandDispatch", IsBackground = true };
        _thread.Start();
    }

    private void OnRegistry(uint opcode, WaylandEventArgs args)
    {
        switch (opcode)
        {
            case RegistryGlobal:
                var added = new WaylandGlobal(args.UInt(0), args.String(1) ?? "", args.UInt(2));
                _globals[added.Name] = added;
                Defer(() => GlobalAdded?.Invoke(added));
                break;
            case RegistryGlobalRemove:
                if (_globals.Remove(args.UInt(0), out var removed)) Defer(() => GlobalRemoved?.Invoke(removed));
                break;
        }
    }

    // the first global of an interface, null when the compositor has none
    internal WaylandGlobal? Find(string @interface)
    {
        lock (_lock) return _globals.Values.Where(g => g.Interface == @interface).MinBy(g => g.Name);
    }

    // wl_registry.bind at the lower of version and what the compositor offers. The handler, if any, is attached in
    // the same lock hold, so no event of the new proxy is dispatched before it.
    internal nint Bind(WaylandGlobal global, string @interface, uint version, WaylandEventHandler? handler = null)
    {
        var table = (WlInterface*)Protocols[@interface];
        // never above the table libwayland decodes the events with
        var bound = Math.Min(Math.Min(version, global.Version), (uint)table->version);
        lock (_lock)
        {
            ThrowIfClosed();
            var args = stackalloc WlArgument[4];
            args[0] = WaylandArgs.UInt(global.Name);
            args[1] = WaylandArgs.String(table->name);
            args[2] = WaylandArgs.UInt(bound);
            args[3] = WaylandArgs.NewId();
            var proxy = wl_proxy_marshal_array_flags(_registry, RegistryBind, (nint)table, bound, 0, args);
            if (proxy == 0) throw new InvalidOperationException($"wl_registry.bind {@interface} failed");
            if (handler != null) Listen(proxy, handler);
            return proxy;
        }
    }

    // a request with no new_id
    internal void Marshal(nint proxy, uint opcode, params ReadOnlySpan<WlArgument> args)
    {
        lock (_lock)
        {
            ThrowIfClosed();
            fixed (WlArgument* pinned = args)
            {
                var none = default(WlArgument);
                wl_proxy_marshal_array_flags(proxy, opcode, 0, wl_proxy_get_version(proxy), 0, args.IsEmpty ? &none : pinned);
            }
        }
    }

    // a request that creates an object of @interface, at the factory's version; args hold a NewId placeholder
    internal nint Create(nint proxy, uint opcode, string @interface, WaylandEventHandler? handler, params ReadOnlySpan<WlArgument> args) =>
        Create(proxy, opcode, Protocols[@interface], @interface, handler, args);

    private nint Create(nint proxy, uint opcode, nint table, string @interface, WaylandEventHandler? handler, ReadOnlySpan<WlArgument> args)
    {
        lock (_lock)
        {
            ThrowIfClosed();
            fixed (WlArgument* pinned = args)
            {
                var created = wl_proxy_marshal_array_flags(proxy, opcode, table, wl_proxy_get_version(proxy), 0, pinned);
                if (created == 0) throw new InvalidOperationException($"creating {@interface} failed");
                if (handler != null) Listen(created, handler);
                return created;
            }
        }
    }

    // the keymap the compositor gives the seat's keyboard (XKB_KEYMAP_FORMAT_TEXT_V1), read over a wl_keyboard of our
    // own; null when the seat has no keyboard or no keymap came within a roundtrip
    internal string? SeatKeymap()
    {
        if (Find(WaylandProtocols.Seat) is not { } global) return null;
        var reader = new SeatKeymapReader();
        nint seat = 0, keyboard = 0;
        try
        {
            seat = Bind(global, WaylandProtocols.Seat, SeatVersion, reader.OnSeat);
            if (!Roundtrip() || !reader.HasKeyboard) return null;
            keyboard = Create(seat, SeatGetKeyboard, _keyboardInterface, "wl_keyboard", reader.OnKeyboard, [WaylandArgs.NewId()]);
            return Roundtrip() ? reader.Keymap : null;
        }
        finally
        {
            if (keyboard != 0)
            {
                if (Version(keyboard) >= 3) Destroy(keyboard, KeyboardRelease);
                else Destroy(keyboard);
            }
            if (seat != 0)
            {
                if (Version(seat) >= 5) Destroy(seat, SeatRelease);
                else Destroy(seat);
            }
            Flush();
        }
    }

    // maps the keymap fd read-only and private, as wl_keyboard.keymap requires; the size counts a terminating NUL
    private static string? ReadKeymap(int fd, uint size)
    {
        if (size is 0 or > int.MaxValue) return null;
        var map = mmap(0, size, PROT_READ, MAP_PRIVATE, fd, 0);
        if (map == MAP_FAILED) return null;
        try
        {
            var text = System.Runtime.InteropServices.Marshal.PtrToStringUTF8(map, (int)size).TrimEnd('\0');
            return text.Length == 0 ? null : text;
        }
        finally
        {
            _ = munmap(map, size);
        }
    }

    // a destructor request: marshals it and frees the proxy
    internal void Destroy(nint proxy, uint opcode)
    {
        lock (_lock)
        {
            _handlers.Remove(proxy);
            if (_display == 0) return;
            var none = default(WlArgument);
            wl_proxy_marshal_array_flags(proxy, opcode, 0, wl_proxy_get_version(proxy), WL_MARSHAL_FLAG_DESTROY, &none);
        }
    }

    // frees a proxy whose interface has no destructor request
    internal void Destroy(nint proxy)
    {
        lock (_lock)
        {
            _handlers.Remove(proxy);
            if (_display == 0) return;
            wl_proxy_destroy(proxy);
        }
    }

    internal uint Version(nint proxy)
    {
        lock (_lock) return wl_proxy_get_version(proxy);
    }

    // sends the buffered requests now rather than at the dispatch thread's next turn
    internal void Flush()
    {
        lock (_lock)
        {
            if (_display != 0) _ = wl_display_flush(_display);
        }
    }

    // blocks until the compositor has handled every request so far and sent its answers; false when the connection failed
    internal bool Roundtrip()
    {
        bool ok;
        lock (_lock)
        {
            ok = _display != 0 && wl_display_roundtrip(_display) >= 0;
        }
        RunDeferred();
        if (!ok) Lose();
        return ok;
    }

    // runs an action once the lock is released; for event handlers raising events of their own
    internal void Defer(Action action)
    {
        lock (_lock) _deferred.Add(action);
    }

    private void Listen(nint proxy, WaylandEventHandler handler)
    {
        _handlers[proxy] = handler;
        if (wl_proxy_add_dispatcher(proxy, &Dispatch, GCHandle.ToIntPtr(_self), 0) != 0)
            throw new InvalidOperationException("the Wayland proxy already has a listener");
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static int Dispatch(nint data, nint target, uint opcode, WlMessage* message, WlArgument* args)
    {
        if (GCHandle.FromIntPtr(data).Target is not WaylandClient client) return 0;
        try
        {
            if (!client._handlers.TryGetValue(target, out var handler)) return 0;
            var signature = System.Runtime.InteropServices.Marshal.PtrToStringUTF8(message->signature) ?? "";
            handler(opcode, new WaylandEventArgs(new ReadOnlySpan<WlArgument>(args, WaylandArgs.Count(signature))));
        }
        catch (Exception e)
        {
            if (client._log.IsEnabled(LogLevel.Warning)) client._log.LogWarning(e, "Dropped a Wayland event its handler threw on");
        }
        return 0;
    }

    private void DispatchLoop()
    {
        while (!_stopping)
        {
            int fd;
            lock (_lock)
            {
                if (_display == 0) return;
                fd = wl_display_get_fd(_display);
            }
            var poll = new PollFd { Fd = fd, Events = NativeMethods.POLLIN };
            NativeMethods.Poll(ref poll, 1, PollTimeoutMs);

            bool ok;
            lock (_lock)
            {
                if (_display == 0) return;
                ok = ReadAndDispatch();
            }
            RunDeferred();
            if (ok) continue;
            Lose();
            return;
        }
    }

    // reads whatever the socket holds without blocking, then dispatches and flushes. Prepare and read (or cancel)
    // stay in one lock hold, so a Roundtrip on another thread never waits on a read this thread left pending.
    private bool ReadAndDispatch()
    {
        while (wl_display_prepare_read(_display) != 0)
            if (wl_display_dispatch_pending(_display) < 0) return false;
        _ = wl_display_flush(_display);
        var poll = new PollFd { Fd = wl_display_get_fd(_display), Events = NativeMethods.POLLIN };
        if (NativeMethods.Poll(ref poll, 1, 0) > 0 && (poll.Revents & NativeMethods.POLLIN) != 0)
        {
            if (wl_display_read_events(_display) < 0) return false;
        }
        else
        {
            wl_display_cancel_read(_display);
            if ((poll.Revents & (NativeMethods.POLLERR | NativeMethods.POLLHUP | NativeMethods.POLLNVAL)) != 0) return false;
        }
        if (wl_display_dispatch_pending(_display) < 0) return false;
        _ = wl_display_flush(_display);
        return wl_display_get_error(_display) == 0;
    }

    private void RunDeferred()
    {
        Action[] run;
        lock (_lock)
        {
            if (_deferred.Count == 0) return;
            run = [.. _deferred];
            _deferred.Clear();
        }
        foreach (var action in run)
        {
            try
            {
                action();
            }
            catch (Exception e)
            {
                if (_log.IsEnabled(LogLevel.Warning)) _log.LogWarning(e, "A Wayland event handler failed");
            }
        }
    }

    private void Lose()
    {
        if (Interlocked.Exchange(ref _lost, 1) != 0 || _stopping) return;
        if (_log.IsEnabled(LogLevel.Warning)) _log.LogWarning("Lost the Wayland display connection");
        Disconnected?.Invoke();
    }

    private void ThrowIfClosed()
    {
        ObjectDisposedException.ThrowIf(_display == 0, this);
    }

    // seat and keyboard events while SeatKeymap waits, on whichever thread dispatches them under the client's lock
    private sealed class SeatKeymapReader
    {
        internal bool HasKeyboard { get; private set; }
        internal string? Keymap { get; private set; }

        internal void OnSeat(uint opcode, WaylandEventArgs args)
        {
            if (opcode == SeatCapabilities) HasKeyboard = (args.UInt(0) & SeatCapabilityKeyboard) != 0;
        }

        internal void OnKeyboard(uint opcode, WaylandEventArgs args)
        {
            if (opcode != KeyboardKeymap) return;
            var fd = args.Fd(1);
            try
            {
                if (args.UInt(0) == KeymapFormatXkbV1) Keymap = ReadKeymap(fd, args.UInt(2));
            }
            finally
            {
                _ = EvdevNativeMethods.Close(fd);
            }
        }
    }

    public void Dispose()
    {
        _stopping = true;
        if (_thread != null && Thread.CurrentThread != _thread) _thread.Join(TimeSpan.FromSeconds(2));
        lock (_lock)
        {
            if (_display == 0) return;
            _handlers.Clear();
            _deferred.Clear();
            if (_registry != 0) wl_proxy_destroy(_registry);
            _registry = 0;
            _ = wl_display_flush(_display);
            wl_display_disconnect(_display);
            _display = 0;
        }
        if (_self.IsAllocated) _self.Free();
    }
}

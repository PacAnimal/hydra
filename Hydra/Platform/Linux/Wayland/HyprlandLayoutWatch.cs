using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging;

namespace Hydra.Platform.Linux.Wayland;

// Hyprland's active layout group, from its own IPC: xdg-desktop-portal-hyprland sends no EIS keyboard modifiers, so the
// master's resolvers would otherwise stay on the first layout. The event socket (.socket2.sock) says a layout switched,
// naming the layout but not its index, so each switch asks the request socket (.socket.sock, "j/devices") for the main
// keyboard's active_layout_index. Each connection to the event socket asks again once it is up, catching up on a switch
// made while it was down
internal sealed class HyprlandLayoutWatch : IDisposable, IAsyncDisposable
{
    private const string RequestSocket = ".socket.sock";
    private const string EventSocket = ".socket2.sock";
    private static readonly TimeSpan QueryTimeout = TimeSpan.FromSeconds(2);
    private static readonly TimeSpan RetryFirst = TimeSpan.FromMilliseconds(250);
    private static readonly TimeSpan RetryMax = TimeSpan.FromSeconds(4);
    private static readonly TimeSpan DisposeWait = TimeSpan.FromSeconds(2);

    private readonly string _dir;
    private readonly Action<HyprlandLayoutWatch, uint> _onGroup;
    private readonly ILogger _log;
    private readonly CancellationTokenSource _stop = new();
    // taken at construction: Dispose disposes _stop, and a Refresh may come after
    private readonly CancellationToken _cancel;
    // one query at a time, the loop's and Refresh's alike, so _group and the warnings see no race and no older answer
    // is reported after a newer one
    private readonly SemaphoreSlim _queryGate = new(1, 1);
    private readonly Lock _refreshLock = new();
    private Task? _refresh;
    // when _refresh was asked for, and when the latest query asked Hyprland, on _time
    private long _refreshFrom;
    private long _lastAsked = long.MinValue;
    private readonly TimeProvider _time;
    private Task _loop = Task.CompletedTask;
    private uint? _group;
    // a failure is warned about once, until a query or a connection succeeds again
    private bool _queryWarned;
    private bool _eventsWarned;
    private bool _startLogged;
    private int _disposed;

    private HyprlandLayoutWatch(string dir, Action<HyprlandLayoutWatch, uint> onGroup, ILogger log, TimeProvider time)
    {
        _dir = dir;
        _onGroup = onGroup;
        _log = log;
        _time = time;
        _cancel = _stop.Token;
    }

    // null off Hyprland, or when its sockets are not there. onGroup names the watch, so a handler can drop a report from
    // one it has detached already; time is the clock Refresh's after is read on
    internal static HyprlandLayoutWatch? Start(Action<HyprlandLayoutWatch, uint> onGroup, ILogger log, TimeProvider time) =>
        WaylandDesktop.IsHyprland && SocketDir() is { } dir ? Start(dir, onGroup, log, time) : null;

    // the directory holding Hyprland's two sockets; null when either is missing
    internal static HyprlandLayoutWatch? Start(string dir, Action<HyprlandLayoutWatch, uint> onGroup, ILogger log, TimeProvider? time = null)
    {
        if (!File.Exists(Path.Combine(dir, RequestSocket)) || !File.Exists(Path.Combine(dir, EventSocket))) return null;
        var watch = new HyprlandLayoutWatch(dir, onGroup, log, time ?? TimeProvider.System);
        var stop = watch._cancel;
        watch._loop = Task.Run(() => watch.Run(stop), CancellationToken.None);
        return watch;
    }

    // tests: a watch on dir whose loop never runs, so only a Refresh asks Hyprland anything
    internal static HyprlandLayoutWatch Unstarted(string dir, Action<HyprlandLayoutWatch, uint> onGroup, ILogger log, TimeProvider time) =>
        new(dir, onGroup, log, time);

    private static string? SocketDir()
    {
        var runtime = Environment.GetEnvironmentVariable("XDG_RUNTIME_DIR");
        var instance = Environment.GetEnvironmentVariable("HYPRLAND_INSTANCE_SIGNATURE");
        return string.IsNullOrEmpty(runtime) || string.IsNullOrEmpty(instance) ? null : Path.Combine(runtime, "hypr", instance);
    }

    // stops the watch; a query under way reports nothing after this, bar one already past its last check (the handler
    // drops that by the watch it names)
    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        _stop.Cancel();
        // the loop holds the token, already cancelled, so it still sees the cancellation
        _stop.Dispose();
    }

    public async ValueTask DisposeAsync()
    {
        Dispose();
        try
        {
            await _loop.WaitAsync(DisposeWait);
        }
        catch (TimeoutException)
        {
            if (_log.IsEnabled(LogLevel.Warning)) _log.LogWarning("Hyprland's layout watch still running at shutdown");
        }
        catch (Exception e)
        {
            // Run catches its own faults, so this is only a last guard: shutdown goes on whatever the loop did
            if (_log.IsEnabled(LogLevel.Warning)) _log.LogWarning(e, "Hyprland's layout watch failed");
        }
    }

    private async Task Run(CancellationToken cancel)
    {
        var failures = 0;
        var faultWarned = false;
        var caughtUp = false;
        while (!cancel.IsCancellationRequested)
        {
            try
            {
                if (!caughtUp) await Query(cancel);
                caughtUp = true;
                if (await Listen(cancel))
                {
                    failures = 0;
                    faultWarned = false;
                }
            }
            catch (OperationCanceledException) when (cancel.IsCancellationRequested)
            {
                return;
            }
            catch (Exception e)
            {
                // a bug or something unforeseen from the socket: the watch carries on rather than stop for good
                if (!faultWarned && _log.IsEnabled(LogLevel.Warning)) _log.LogWarning(e, "Following Hyprland's keyboard layout failed; retrying");
                faultWarned = true;
            }
            try
            {
                await Task.Delay(WaylandBarriers.Backoff(failures++, RetryFirst, RetryMax), cancel);
            }
            catch (OperationCanceledException)
            {
                return;
            }
        }
    }

    // one connection to the event socket, until it ends; true when it connected
    private async Task<bool> Listen(CancellationToken cancel)
    {
        Socket socket;
        try
        {
            socket = await Connect(EventSocket, cancel);
        }
        catch (Exception e) when (e is SocketException or IOException)
        {
            if (!_eventsWarned && _log.IsEnabled(LogLevel.Warning))
                _log.LogWarning("Cannot connect to Hyprland's event socket ({Error}); the keyboard layout is not followed until it answers", e.Message);
            _eventsWarned = true;
            return false;
        }
        _eventsWarned = false;
        using (socket)
        {
            // a switch while disconnected is caught up on here
            await Query(cancel);
            await using var stream = new NetworkStream(socket, ownsSocket: false);
            using var reader = new StreamReader(stream, Encoding.UTF8);
            try
            {
                while (await reader.ReadLineAsync(cancel) is { } line)
                {
                    if (IsLayoutEvent(line)) await Query(cancel);
                }
            }
            catch (IOException) when (!cancel.IsCancellationRequested)
            {
            }
        }
        if (!cancel.IsCancellationRequested && _log.IsEnabled(LogLevel.Debug)) _log.LogDebug("Hyprland's event socket closed; reconnecting");
        return true;
    }

    // the main keyboard's group, reported when it changed
    private async Task Query(CancellationToken cancel)
    {
        await _queryGate.WaitAsync(cancel);
        try
        {
            await QueryGated(cancel);
        }
        finally
        {
            _queryGate.Release();
        }
    }

    private async Task QueryGated(CancellationToken cancel)
    {
        uint? group;
        Volatile.Write(ref _lastAsked, _time.GetTimestamp());
        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancel);
            timeout.CancelAfter(QueryTimeout);
            group = GroupFromDevices(await Request("j/devices", timeout.Token));
        }
        catch (Exception e) when (!cancel.IsCancellationRequested && e is SocketException or IOException or OperationCanceledException)
        {
            WarnQuery(e is OperationCanceledException ? "no answer" : e.Message);
            return;
        }
        if (group is not { } found)
        {
            WarnQuery("no keyboard with a layout index in its devices");
            return;
        }
        _queryWarned = false;
        if (!_startLogged && _log.IsEnabled(LogLevel.Information)) _log.LogInformation("Keyboard layout: following Hyprland's active layout");
        _startLogged = true;
        if (_group == found || cancel.IsCancellationRequested) return;
        _group = found;
        _onGroup(this, found);
    }

    // asks j/devices once more, off the caller's thread: Hyprland sends no activelayout event when the user moves to
    // another keyboard on another group. after is the caller's time (on the watch's TimeProvider): a query asked at or
    // after it already answers for it, and a Refresh while one asked since is under way joins that one; one asked before
    // may predate a switch the caller saw, so another follows it
    internal Task Refresh(long after)
    {
        lock (_refreshLock)
        {
            if (Volatile.Read(ref _disposed) != 0) return Task.CompletedTask;
            if (_refresh is { IsCompleted: false } running)
            {
                if (_refreshFrom >= after) return running;
            }
            else if (Volatile.Read(ref _lastAsked) >= after)
            {
                return Task.CompletedTask;
            }
            // the gate queues it behind any query under way, so it asks no earlier than now
            _refreshFrom = _time.GetTimestamp();
            _refresh = Task.Run(RefreshOnce, CancellationToken.None);
            return _refresh;
        }
    }

    private async Task RefreshOnce()
    {
        try
        {
            await Query(_cancel);
        }
        catch (OperationCanceledException) when (_cancel.IsCancellationRequested)
        {
        }
        catch (Exception e)
        {
            if (_log.IsEnabled(LogLevel.Warning)) _log.LogWarning(e, "Asking Hyprland for the active keyboard layout failed");
        }
    }

    private void WarnQuery(string error)
    {
        if (!_queryWarned && _log.IsEnabled(LogLevel.Warning)) _log.LogWarning("Asking Hyprland for the active keyboard layout failed ({Error})", error);
        _queryWarned = true;
    }

    private async Task<string> Request(string request, CancellationToken cancel)
    {
        using var socket = await Connect(RequestSocket, cancel);
        await socket.SendAsync(Encoding.UTF8.GetBytes(request), SocketFlags.None, cancel);
        await using var stream = new NetworkStream(socket, ownsSocket: false);
        using var reader = new StreamReader(stream, Encoding.UTF8);
        return await reader.ReadToEndAsync(cancel);
    }

    private async Task<Socket> Connect(string name, CancellationToken cancel)
    {
        var socket = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
        try
        {
            await socket.ConnectAsync(new UnixDomainSocketEndPoint(Path.Combine(_dir, name)), cancel);
            return socket;
        }
        catch
        {
            socket.Dispose();
            throw;
        }
    }

    // -- the pure parts --

    // the active_layout_index of the main keyboard in "j/devices", else of the first; null when there is none or the
    // JSON does not parse
    internal static uint? GroupFromDevices(string json)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            if (doc.RootElement.ValueKind != JsonValueKind.Object
                || !doc.RootElement.TryGetProperty("keyboards", out var keyboards)
                || keyboards.ValueKind != JsonValueKind.Array) return null;
            JsonElement? first = null;
            foreach (var keyboard in keyboards.EnumerateArray())
            {
                if (keyboard.ValueKind != JsonValueKind.Object) continue;
                first ??= keyboard;
                if (keyboard.TryGetProperty("main", out var main) && main.ValueKind == JsonValueKind.True) return Index(keyboard);
            }
            return first is { } fallback ? Index(fallback) : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static uint? Index(JsonElement keyboard) =>
        keyboard.TryGetProperty("active_layout_index", out var index) && index.ValueKind == JsonValueKind.Number && index.TryGetUInt32(out var group)
            ? group
            : null;

    // an event socket line saying a keyboard's layout switched: "activelayout>>KEYBOARD,LAYOUT", or its v2
    internal static bool IsLayoutEvent(string line) =>
        line.StartsWith("activelayout>>", StringComparison.Ordinal) || line.StartsWith("activelayoutv2>>", StringComparison.Ordinal);
}

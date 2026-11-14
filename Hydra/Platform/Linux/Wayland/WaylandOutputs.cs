using Hydra.Screen;
using Microsoft.Extensions.Logging;
using static Hydra.Platform.Linux.Wayland.WaylandProtocols;

namespace Hydra.Platform.Linux.Wayland;

// the compositor's outputs in logical layout coordinates: wl_output for each monitor, zxdg_output_v1 for its logical
// position, size, name and description. An output's state is committed on wl_output.done (or xdg_output.done before
// xdg_output v3), so a change seen through several events publishes once. Without an xdg_output_manager the layout
// falls back to wl_output's geometry position and its current mode divided by its scale.
internal sealed class WaylandOutputs : IDisposable
{
    private const uint OutputVersion = 4;
    private const uint XdgManagerVersion = 3;

    // -- wl_output events and requests --

    private const uint OutputGeometry = 0;
    private const uint OutputMode = 1;
    private const uint OutputDone = 2;
    private const uint OutputScale = 3;
    private const uint OutputNameEvent = 4;
    private const uint OutputDescriptionEvent = 5;
    private const uint OutputRelease = 3;
    private const uint ModeCurrent = 0x1;

    private readonly WaylandClient _client;
    private readonly ILogger _log;
    private readonly Lock _lock = new();
    private readonly Dictionary<uint, Monitor> _monitors = [];
    private nint _xdgManager;
    private bool _disposed;
    private IReadOnlyList<DetectedScreen> _screens = [];

    // raised off the dispatch thread's lock when an output appears, goes or changes geometry or name
    public event Action? Changed;

    internal WaylandOutputs(WaylandClient client, ILogger log)
    {
        _client = client;
        _log = log;
        _client.GlobalAdded += OnGlobalAdded;
        _client.GlobalRemoved += OnGlobalRemoved;
        if (_client.Find(XdgOutputManager) is { } manager)
            _xdgManager = _client.Bind(manager, XdgOutputManager, XdgManagerVersion);
        foreach (var global in _client.Globals) OnGlobalAdded(global);
        // the first answers the binds, the second the xdg_outputs created from them
        _client.Roundtrip();
        _client.Roundtrip();
    }

    internal IReadOnlyList<DetectedScreen> Screens
    {
        get
        {
            lock (_lock) return _screens;
        }
    }

    // each region (an InputCapture zone, an EIS region) named after the output it lies on: one of the same bounds,
    // else one at the same origin (a size in other units); "{fallback}-N" when no named output is left to match
    internal static List<DetectedScreen> Name(IReadOnlyList<ScreenBounds> regions, IReadOnlyList<DetectedScreen> outputs, string fallback)
    {
        var free = outputs.Where(o => !string.IsNullOrEmpty(o.OutputName)).ToList();
        var matched = new DetectedScreen?[regions.Count];
        for (var i = 0; i < regions.Count; i++)
        {
            var region = regions[i];
            matched[i] = Take(free, o => o.Bounds == region);
        }
        for (var i = 0; i < regions.Count; i++)
        {
            var region = regions[i];
            matched[i] ??= Take(free, o => o.X == region.X && o.Y == region.Y);
        }
        return
        [
            .. regions.Select((r, i) => matched[i] is { } output
                ? new DetectedScreen(r.X, r.Y, r.Width, r.Height, output.DisplayName, output.OutputName, output.PlatformId)
                : new DetectedScreen(r.X, r.Y, r.Width, r.Height, null, $"{fallback}-{i}", null)),
        ];
    }

    private static DetectedScreen? Take(List<DetectedScreen> free, Func<DetectedScreen, bool> match)
    {
        var index = free.FindIndex(o => match(o));
        if (index < 0) return null;
        var output = free[index];
        free.RemoveAt(index);
        return output;
    }

    private void OnGlobalAdded(WaylandGlobal global)
    {
        if (global.Interface != Output) return;
        var monitor = new Monitor(global.Name);
        lock (_lock)
        {
            if (_disposed || !_monitors.TryAdd(global.Name, monitor)) return;
        }
        try
        {
            monitor.Output = _client.Bind(global, Output, OutputVersion, (opcode, args) => OnOutput(monitor, opcode, args));
            if (_xdgManager != 0)
            {
                monitor.XdgOutput = _client.Create(_xdgManager, XdgOutputManagerGet, XdgOutput,
                    (opcode, args) => OnXdgOutput(monitor, opcode, args),
                    WaylandArgs.NewId(), WaylandArgs.Object(monitor.Output));
            }
            _client.Flush();
        }
        catch (Exception e) when (e is InvalidOperationException or ObjectDisposedException)
        {
            if (_log.IsEnabled(LogLevel.Warning)) _log.LogWarning(e, "Cannot follow Wayland output {Name}", global.Name);
            lock (_lock) _monitors.Remove(global.Name);
        }
    }

    private void OnGlobalRemoved(WaylandGlobal global)
    {
        Monitor? monitor;
        lock (_lock)
        {
            if (!_monitors.Remove(global.Name, out monitor)) return;
        }
        Release(monitor);
        Publish();
    }

    // -- events, on the dispatch thread under the client's lock --

    private void OnOutput(Monitor monitor, uint opcode, WaylandEventArgs args)
    {
        switch (opcode)
        {
            case OutputGeometry:
                monitor.Pending.GeometryX = args.Int(0);
                monitor.Pending.GeometryY = args.Int(1);
                break;
            case OutputMode when (args.UInt(0) & ModeCurrent) != 0:
                monitor.Pending.ModeWidth = args.Int(1);
                monitor.Pending.ModeHeight = args.Int(2);
                break;
            case OutputScale:
                monitor.Pending.Scale = Math.Max(1, args.Int(0));
                break;
            case OutputNameEvent:
                monitor.Pending.OutputName = args.String(0);
                break;
            case OutputDescriptionEvent:
                monitor.Pending.OutputDescription = args.String(0);
                break;
            case OutputDone:
                Commit(monitor);
                break;
        }
    }

    private void OnXdgOutput(Monitor monitor, uint opcode, WaylandEventArgs args)
    {
        switch (opcode)
        {
            case XdgOutputLogicalPosition:
                monitor.Pending.LogicalX = args.Int(0);
                monitor.Pending.LogicalY = args.Int(1);
                break;
            case XdgOutputLogicalSize:
                monitor.Pending.LogicalWidth = args.Int(0);
                monitor.Pending.LogicalHeight = args.Int(1);
                break;
            case XdgOutputName:
                monitor.Pending.XdgName = args.String(0);
                break;
            case XdgOutputDescription:
                monitor.Pending.XdgDescription = args.String(0);
                break;
            case XdgOutputDone:
                Commit(monitor);
                break;
        }
    }

    private void Commit(Monitor monitor)
    {
        var screen = monitor.Pending.ToScreen(_xdgManager != 0);
        lock (_lock)
        {
            if (screen == monitor.Screen) return;
            monitor.Screen = screen;
        }
        _client.Defer(Publish);
    }

    private void Publish()
    {
        bool changed;
        lock (_lock)
        {
            IReadOnlyList<DetectedScreen> screens =
            [
                .. _monitors.Values
                    .Where(m => m.Screen != null)
                    .OrderBy(m => m.Screen!.X).ThenBy(m => m.Screen!.Y).ThenBy(m => m.GlobalName)
                    .Select(m => m.Screen!),
            ];
            changed = !screens.SequenceEqual(_screens);
            _screens = screens;
        }
        if (changed) Changed?.Invoke();
    }

    private void Release(Monitor monitor)
    {
        if (monitor.XdgOutput != 0) _client.Destroy(monitor.XdgOutput, XdgOutputDestroy);
        if (monitor.Output == 0) return;
        if (_client.Version(monitor.Output) >= 3) _client.Destroy(monitor.Output, OutputRelease);
        else _client.Destroy(monitor.Output);
    }

    public void Dispose()
    {
        _client.GlobalAdded -= OnGlobalAdded;
        _client.GlobalRemoved -= OnGlobalRemoved;
        List<Monitor> monitors;
        lock (_lock)
        {
            _disposed = true;
            monitors = [.. _monitors.Values];
            _monitors.Clear();
            _screens = [];
        }
        foreach (var monitor in monitors) Release(monitor);
        if (_xdgManager != 0) _client.Destroy(_xdgManager, XdgOutputManagerDestroy);
        _xdgManager = 0;
        _client.Flush();
    }

    private sealed class Monitor(uint name)
    {
        internal uint GlobalName { get; } = name;
        internal nint Output { get; set; }
        internal nint XdgOutput { get; set; }
        internal OutputState Pending { get; } = new();
        internal DetectedScreen? Screen { get; set; }
    }

    // what the events so far said about one output
    private sealed class OutputState
    {
        internal int GeometryX { get; set; }
        internal int GeometryY { get; set; }
        internal int ModeWidth { get; set; }
        internal int ModeHeight { get; set; }
        internal int Scale { get; set; } = 1;
        internal string? OutputName { get; set; }
        internal string? OutputDescription { get; set; }
        internal int? LogicalX { get; set; }
        internal int? LogicalY { get; set; }
        internal int? LogicalWidth { get; set; }
        internal int? LogicalHeight { get; set; }
        internal string? XdgName { get; set; }
        internal string? XdgDescription { get; set; }

        // null until the output has a size; with xdg-output, until its logical size has come
        internal DetectedScreen? ToScreen(bool xdg)
        {
            int x, y, width, height;
            if (xdg)
            {
                if (LogicalWidth is not { } w || LogicalHeight is not { } h) return null;
                x = LogicalX ?? 0;
                y = LogicalY ?? 0;
                width = w;
                height = h;
            }
            else
            {
                x = GeometryX;
                y = GeometryY;
                width = ModeWidth / Scale;
                height = ModeHeight / Scale;
            }
            if (width <= 0 || height <= 0) return null;
            var name = XdgName ?? OutputName;
            return new DetectedScreen(x, y, width, height, XdgDescription ?? OutputDescription, name, name);
        }
    }
}

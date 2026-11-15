using Hydra.Screen;

namespace Hydra.Platform.Linux.Wayland;

// a Wayland slave's readiness and screens, under its session's gate: ready once the device can type and point, known
// once the screens are worth reporting (ScreensKnown)
internal sealed class SlaveScreenState(Lock gate, Func<bool> isLost)
{
    private TaskCompletionSource _ready = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private TaskCompletionSource _known = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private IReadOnlyList<DetectedScreen>? _regions;

    // a slave's screens are worth reporting once there are regions, which an EIS absolute pointer has before it is
    // resumed; with none yet (a relative pointer only, or the wlroots path) once the device can type and point
    internal static bool ScreensKnown(bool ready, IReadOnlyList<DetectedScreen>? regions) => ready || regions is { Count: > 0 };

    // completes while the device can type and point
    internal Task Ready()
    {
        lock (gate) return _ready.Task;
    }

    // completes while Regions() is worth reporting
    internal Task Known()
    {
        lock (gate) return _known.Task;
    }

    internal IReadOnlyList<DetectedScreen> Regions()
    {
        lock (gate) return _regions ?? [];
    }

    internal void Lost()
    {
        lock (gate)
        {
            _ready.TrySetException(new PortalException("The Wayland portal session was lost"));
            _known.TrySetException(new PortalException("The Wayland portal session was lost"));
            // nobody may be waiting on them
            _ = _ready.Task.Exception;
            _ = _known.Task.Exception;
        }
    }

    // records readiness and, when known, the screens; a removed absolute pointer (null) keeps the last ones. true
    // when the screens changed
    internal bool Update(bool ready, IReadOnlyList<DetectedScreen>? regions)
    {
        lock (gate)
        {
            if (isLost()) return false;
            if (ready) _ready.TrySetResult();
            else if (_ready.Task.IsCompleted) _ready = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var changed = false;
            if (regions != null)
            {
                changed = _regions != null && ScreenRect.ScreenListChanged(regions, _regions);
                _regions = regions;
            }
            if (ScreensKnown(ready, _regions)) _known.TrySetResult();
            else if (_known.Task.IsCompleted) _known = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            return changed;
        }
    }
}

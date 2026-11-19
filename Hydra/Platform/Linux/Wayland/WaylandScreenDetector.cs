using Hydra.Config;
using Hydra.Screen;
using Microsoft.Extensions.Logging;

namespace Hydra.Platform.Linux.Wayland;

// Monitors as the compositor lays them out, from the portal session. The base polls Detect, which answers the last
// layout the session gave; the session is asked again only when it says the screens changed (or a fresh session
// started), so the poll never starts a session or shows a dialog. A failed read keeps the last layout, and until the
// first read succeeds nothing is published: an empty layout would drop every local screen.
internal sealed class WaylandScreenDetector(IWaylandSession session, IHydraProfile profile, ILogger<WaylandScreenDetector> log)
    : ScreenDetector(profile, log)
{
    private static readonly TimeSpan RetryDelay = TimeSpan.FromSeconds(30);

    private readonly ILogger _log = log;

    private List<DetectedScreen>? _last;
    private volatile bool _due = true;
    private DateTime _retryAt = DateTime.MinValue;
    private bool _subscribed;

    protected override async Task Execute(CancellationToken cancel)
    {
        if (!_subscribed)
        {
            _subscribed = true;
            session.ScreensChanged += OnScreensChanged;
        }
        if (_due && DateTime.UtcNow >= _retryAt) await Refresh(cancel);
        if (_last != null) await base.Execute(cancel);
    }

    private void OnScreensChanged()
    {
        _due = true;
        _retryAt = DateTime.MinValue;
        Trigger();
    }

    private async Task Refresh(CancellationToken cancel)
    {
        _due = false;
        try
        {
            var screens = await session.Screens(cancel);
            if (screens.Count > 0) _last = [.. screens];
            else if (_log.IsEnabled(LogLevel.Debug)) _log.LogDebug("The Wayland session reported no screens; keeping the last layout");
        }
        catch (Exception e) when (e is not OperationCanceledException || !cancel.IsCancellationRequested)
        {
            _due = true;
            _retryAt = DateTime.UtcNow + RetryDelay;
            if (_log.IsEnabled(LogLevel.Warning)) _log.LogWarning("Reading the screens from the Wayland session failed: {Error}", e.Message);
        }
    }

    protected override List<DetectedScreen> Detect() => _last ?? [];

    protected override Task OnShutdown(CancellationToken cancel)
    {
        session.ScreensChanged -= OnScreensChanged;
        return Task.CompletedTask;
    }
}

using Cathedral.Utils;
using Hydra.Relay;
using Microsoft.Extensions.Logging;

namespace Hydra.Platform;

// starts a passive event tap on the slave to track local keyboard/mouse activity.
// all events pass through unchanged — nothing is consumed or forwarded.
internal sealed class SlaveLocalInputWatcher(ILocalEventTap tap, IActivityTracker tracker, ILogger<SlaveLocalInputWatcher> log)
    : SimpleHostedService(log)
{
    protected override async Task Execute(CancellationToken cancel)
    {
        if (!tap.IsAccessibilityTrusted())
        {
            log.LogWarning("Accessibility permission not granted — open System Settings › Privacy & Security › Accessibility and enable Hydra, then Hydra will continue automatically.");
            await tap.WaitForAccessibilityTrusted(cancel);
            if (cancel.IsCancellationRequested) return;
            log.LogInformation("Accessibility permission granted");
        }

        void OnActivity() => Background.RunValueTask(tracker.LocalActivity, log, cancel);
        await tap.StartEventTap(
            // mouse moves intentionally count: the slave cursor is hidden while a master is on-screen,
            // so moves only fire when the user is genuinely sitting at the slave machine
            onMouseMove: (_, _) => OnActivity(),
            onMouseDelta: null,
            onKeyEvent: _ => OnActivity(),
            onMouseButton: _ => OnActivity(),
            onMouseScroll: _ => OnActivity());
    }

    protected override Task OnShutdown(CancellationToken cancel)
    {
        tap.StopEventTap();
        return Task.CompletedTask;
    }
}

using Microsoft.Extensions.Logging;

namespace Hydra.Platform.Linux;

// manages the grab/ungrab/retry lifecycle for a single X11 grab (keyboard or pointer).
// the grab and ungrab actions are provided by the caller so this class stays decoupled from
// the specific X11 functions and parameters used for each grab type.
internal sealed class XGrabSession(
    string label, Lock @lock, ILogger log,
    Action? preGrab, Func<int> grab, Action? preRetry, Action ungrab)
{
    private bool _grabbed;
    private CancellationTokenSource? _retryCts;

    public void Grab()
    {
        lock (@lock) { if (_grabbed) return; }
        preGrab?.Invoke();
        var result = grab();
        if (result == NativeMethods.GrabSuccess)
        {
            lock (@lock) _grabbed = true;
            return;
        }
        log.LogWarning("XGrab{Label} failed (result={Result}), retrying in background", label, result);
        CancellationTokenSource cts;
        lock (@lock)
        {
            _retryCts?.Cancel();
            _retryCts?.Dispose();
            cts = _retryCts = new CancellationTokenSource();
        }
        _ = Task.Run(() => RetryAsync(cts.Token));
    }

    public void Ungrab()
    {
        CancellationTokenSource? cts;
        bool wasGrabbed;
        lock (@lock)
        {
            cts = _retryCts;
            _retryCts = null;
            wasGrabbed = _grabbed;
            _grabbed = false;
        }
        cts?.Cancel();
        cts?.Dispose();
        if (!wasGrabbed) return;
        ungrab();
    }

    private async Task RetryAsync(CancellationToken ct)
    {
        const int maxAttempts = 20;
        for (var i = 0; i < maxAttempts; i++)
        {
            try { await Task.Delay(50, ct); }
            catch (OperationCanceledException) { return; }

            preRetry?.Invoke();
            var result = grab();

            if (result != NativeMethods.GrabSuccess)
            {
                log.LogDebug("XGrab{Label} retry {Attempt}/{Max} failed (result={Result})", label, i + 1, maxAttempts, result);
                continue;
            }

            lock (@lock)
            {
                if (ct.IsCancellationRequested)
                {
                    // Ungrab ran while we were retrying — release immediately
                    ungrab();
                    return;
                }
                _grabbed = true;
            }
            log.LogInformation("XGrab{Label} succeeded on retry {Attempt}", label, i + 1);
            return;
        }
        log.LogWarning("XGrab{Label} failed after {Max} retries", label, maxAttempts);
    }
}

namespace Hydra.Management;

/// <summary>
/// Re-stamps the private sidecar when a user signs in at the console, since its DACL names only whoever was
/// there at its last write. Never without a console user: that stamp would drop the one it had. A stamp that
/// fails is retried, waiting twice as many polls after each failure in a row, unless another user signs in. The
/// stamp is told whether it retries a failure, so only the first failure of a streak need be loud.
/// </summary>
internal sealed class ConsoleSignInRestamper(Func<string?> consoleUser, Func<bool, Task<bool>> restamp)
{
    internal const int FirstRetryPolls = 4;
    internal const int MaxRetryPolls = 240;

    private string? _observed;
    private string? _failed;
    private Task<bool>? _stamp;
    private int _backoff = FirstRetryPolls;
    private int _waiting;

    // call from one thread, once a poll; returns the stamp in flight, which never faults
    internal Task Check()
    {
        if (_stamp is { IsCompleted: false }) return _stamp;
        if (_stamp != null) Settle(_stamp.Result);
        _stamp = null;

        var user = consoleUser();
        if (_failed != null && user != null && !user.Equals(_failed, StringComparison.Ordinal)) EndStreak();
        if (_waiting > 0)
        {
            _waiting--;
            return Task.CompletedTask;
        }

        var signedIn = user != null && !user.Equals(_observed, StringComparison.Ordinal);
        _observed = user;
        if (!signedIn) return Task.CompletedTask;
        return _stamp = Succeeded(retry: _failed != null);
    }

    private void Settle(bool succeeded)
    {
        if (succeeded)
        {
            EndStreak();
            return;
        }
        // forgotten, so the next poll after the wait stamps them again
        _failed = _observed;
        _observed = null;
        _waiting = _backoff;
        _backoff = Math.Min(_backoff * 2, MaxRetryPolls);
    }

    private void EndStreak()
    {
        _failed = null;
        _waiting = 0;
        _backoff = FirstRetryPolls;
    }

    // the caller logs its own failures; here a fault only means try again
    private async Task<bool> Succeeded(bool retry)
    {
        try
        {
            return await restamp(retry);
        }
        catch (Exception)
        {
            return false;
        }
    }
}

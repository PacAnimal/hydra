namespace Hydra.Platform.Linux.Wayland;

// The portal finds a desktop file just written only once GLib's file monitor has told its desktop-file index, so a
// Register right after the write can answer "App info not found". This is the schedule it is retried on then.
internal static class RegisterBackoff
{
    internal static readonly TimeSpan First = TimeSpan.FromMilliseconds(50);
    internal static readonly TimeSpan Total = TimeSpan.FromSeconds(2);

    // first, doubling each time, the last cut short so the waits add up to total
    internal static IEnumerable<TimeSpan> Delays(TimeSpan first, TimeSpan total)
    {
        var left = total;
        for (var delay = first; delay > TimeSpan.Zero && left > TimeSpan.Zero; delay *= 2)
        {
            var wait = delay < left ? delay : left;
            yield return wait;
            left -= wait;
        }
    }

    // attempt until it succeeds, waiting each delay in turn before the next try; false when every try failed
    internal static async Task<bool> Retry(Func<Task<bool>> attempt, IEnumerable<TimeSpan> delays, TimeProvider time, CancellationToken cancel)
    {
        if (await attempt()) return true;
        foreach (var delay in delays)
        {
            await Task.Delay(delay, time, cancel);
            if (await attempt()) return true;
        }
        return false;
    }
}

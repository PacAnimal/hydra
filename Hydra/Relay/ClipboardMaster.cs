using Cathedral.Extensions;
using Hydra.Platform;
using Microsoft.Extensions.Logging;

namespace Hydra.Relay;

// the master's side of the clipboard handshake, and what it keeps between the slaves' messages
public sealed class ClipboardMaster(IClipboardSync sync, ILogger log)
{
    private readonly EchoFallback _echo = new(sync, log);
    private PendingPull? _pull;
    private int _ids;

    // what to offer a slave on entering it; null for nothing
    public ClipboardHashMessage? Hash() => NonEmpty("hash") is { } clip ? ClipboardWire.ToHash(clip) : null;

    // the push a slave asked for; null for nothing
    public ClipboardPushMessage? Push() => NonEmpty("push") is { } clip ? ClipboardWire.ToPush(clip) : null;

    public ClipboardPullMessage Pull(string host)
    {
        var against = _echo.Baseline("pull");
        var id = Interlocked.Increment(ref _ids);
        Volatile.Write(ref _pull, new PendingPull(id, host, against));
        return ClipboardWire.ToPull(against, id);
    }

    // a slave's answer: what it applied; null for nothing, or for an answer to any pull but the latest, from any host but
    // the one it went to, or a second time
    public ClipboardSnapshot? OnPullAnswer(string host, ClipboardPullResponseMessage answer)
    {
        var pull = Volatile.Read(ref _pull);
        if (pull == null || !host.EqualsIgnoreCase(pull.Host) || answer.PullId != pull.Id || Interlocked.CompareExchange(ref _pull, null, pull) != pull)
        {
            if (log.IsEnabled(LogLevel.Debug))
                log.LogDebug("Clipboard pull response from {Host} ignored (not the answer to the latest pull)", host);
            return null;
        }
        if (log.IsEnabled(LogLevel.Debug))
            log.LogDebug("Clipboard pull response from {Host}: {Answer}", host, ClipboardWire.Describe(answer));

        var applied = ClipboardWire.TakePullAnswer(sync, answer, pull.Against, log, host);
        _echo.Remember(applied);
        return applied;
    }

    private ClipboardSnapshot? NonEmpty(string context) => _echo.Read(context) is { IsEmpty: false } clip ? clip : null;

    private sealed record PendingPull(int Id, string Host, ClipboardBaseline Against);
}

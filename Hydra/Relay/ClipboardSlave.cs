using Hydra.Platform;
using Microsoft.Extensions.Logging;

namespace Hydra.Relay;

// the slave's side of the clipboard handshake, and what it keeps between the master's messages
public sealed class ClipboardSlave(IClipboardSync sync, ILogger log)
{
    private readonly EchoFallback _echo = new(sync, log);
    private ClipboardBaseline? _selectionPushFor;

    // the master's hash, against this host's clipboard: what it needs pushed
    public PushNeed OnHash(ClipboardHashMessage hash, string host)
    {
        var local = _echo.Baseline("hash check");
        var need = ClipboardWire.NeedsPush(hash, sync, local, log, host);
        _selectionPushFor = need == PushNeed.Selection ? local : null;
        return need;
    }

    public void OnPush(ClipboardPushMessage push, string host)
    {
        if (log.IsEnabled(LogLevel.Debug))
            log.LogDebug("Clipboard push from {Host}: {Push}", host, ClipboardWire.Describe(push));
        _echo.Remember(ClipboardWire.ApplyPush(sync, push, _selectionPushFor, log, host));
        _selectionPushFor = null;
    }

    public ClipboardPullResponseMessage OnPull(ClipboardPullMessage? pull, string host)
    {
        var answer = ClipboardWire.AnswerPull(_echo.Read("pull response"), pull);
        if (log.IsEnabled(LogLevel.Debug))
            log.LogDebug("Clipboard pull to {Host}: {Answer}", host, ClipboardWire.Describe(answer));
        return answer;
    }
}

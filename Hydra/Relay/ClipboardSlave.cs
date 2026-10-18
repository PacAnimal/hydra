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
        var local = hash.Hash == null ? ClipboardBaseline.SelectionOf(sync) : _echo.Baseline("hash check");
        var need = ClipboardWire.NeedsPush(hash, sync, local, log, host);
        _selectionPushFor = need == PushNeed.Selection ? local : null;
        if (!log.IsEnabled(LogLevel.Debug)) return need;
        if (need == PushNeed.None)
            log.LogDebug("Clipboard hash from {Host}: matches", host);
        else
            log.LogDebug("Clipboard hash from {Host}: {Need} differs, requesting push", host, need);
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

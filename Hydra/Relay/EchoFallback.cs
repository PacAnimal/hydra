using Hydra.Platform;
using Microsoft.Extensions.Logging;

namespace Hydra.Relay;

// a host's echo fallback: what it last took from a peer, read back where ReadAll finds no format while the clipboard is
// still Hydra's write
public sealed class EchoFallback(IClipboardSync sync, ILogger log)
{
    private ClipboardSnapshot? _last;

    // the clipboard, or null when it could not be read, which is unknown, never empty
    public ClipboardSnapshot? Read(string context) => ClipboardUtils.ReadWithFallback(sync, Volatile.Read(ref _last), log, context);

    // what a peer's clipboard is compared against; null when the clipboard could not be read
    public ClipboardBaseline? Baseline(string context) => Read(context) is { } read ? ClipboardBaseline.Of(read) : null;

    // what was applied from a peer, as the clipboard took it: a selection alone leaves the clipboard, and so the
    // fallback, as it was
    public void Remember(ClipboardSnapshot? applied)
    {
        if (applied is { IsSelectionOnly: false }) Volatile.Write(ref _last, applied);
    }

    // the clipboard was emptied another way, as by the placeholder written in place of a refused concealed copy
    public void Forget() => Volatile.Write(ref _last, null);

    // empties the clipboard, which stays Hydra's own write, so a read finds it empty, not what was last taken; whether
    // the clear took, the fallback forgotten either way
    public bool Empty()
    {
        var cleared = sync.ClearClipboard();
        Volatile.Write(ref _last, null);
        return cleared;
    }

    // copied files are kept, as is a clipboard keep says already matches; else empty empties it
    public BeginDecision EmptyUnlessFiles(string what, string host, Func<bool> empty, Func<bool>? keep = null)
    {
        if (sync.HasFileClipboard())
        {
            if (log.IsEnabled(LogLevel.Debug))
                log.LogDebug("Clipboard {What} from {Host}: the clipboard holds files, leaving it", what, host);
            return BeginDecision.Keep;
        }
        if (keep?.Invoke() == true) return BeginDecision.Keep;
        return empty() ? BeginDecision.Cleared : BeginDecision.ClearFailed;
    }

    // a Begin from host was rejected: begun only on Hydra's own write, a copy made here since being newer than whatever
    // it said was coming
    public BeginDecision Rejected(string host, Func<BeginDecision> begun)
    {
        if (ClipboardChanges.Of(sync).StillOwnWrite() ?? sync.CanUseEchoFallback()) return begun();
        if (log.IsEnabled(LogLevel.Debug))
            log.LogDebug("Clipboard stream from {Host} rejected: the clipboard is not Hydra's write, keeping it", host);
        return BeginDecision.Keep;
    }
}

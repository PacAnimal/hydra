using Hydra.Platform;
using Microsoft.Extensions.Logging;

namespace Hydra.Relay;

// a host's echo fallback: what it last took from a peer, read back where Get* returns null because Hydra wrote it
public sealed class EchoFallback(IClipboardSync sync, ILogger log)
{
    private ClipboardSnapshot? _last;

    public ClipboardSnapshot Read(string context) => ClipboardUtils.ReadWithFallback(sync, Volatile.Read(ref _last), log, context);

    // what a peer's clipboard is compared against
    public ClipboardBaseline Baseline(string context) => ClipboardBaseline.Of(Read(context));

    public void Remember(ClipboardSnapshot? applied)
    {
        if (applied != null) Volatile.Write(ref _last, ClipboardUtils.Remember(Volatile.Read(ref _last), applied));
    }
}

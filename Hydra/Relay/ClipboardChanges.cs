using System.Runtime.CompilerServices;
using Hydra.Platform;

namespace Hydra.Relay;

// the OS clipboard's change counter and the count Hydra's own last write left on it, one per clipboard, so a write made
// as master or as slave is never taken for the user's copy by the other. A copy since a point is the counter moved past
// both; every answer is null where the platform keeps no counter
public sealed class ClipboardChanges
{
    private static readonly ConditionalWeakTable<IClipboardSync, ClipboardChanges> All = [];

    private readonly IClipboardSync _sync;
    private readonly Lock _lock = new();
    private long? _wroteAt;

    private ClipboardChanges(IClipboardSync sync) => _sync = sync;

    public static ClipboardChanges Of(IClipboardSync sync) => All.GetValue(sync, s => new ClipboardChanges(s));

    public long? Now() => _sync.ChangeCount();

    // Hydra's own write, noting the count it left only when wrote says it took: the count the platform reports for that
    // write, else the counter after it, so a failed write, a selection alone, or a copy landing during the write is never
    // taken for Hydra's own
    public T Write<T>(Func<T> write, Func<T, bool> wrote)
    {
        var result = write();
        if (wrote(result) && (_sync.WroteAt() ?? _sync.ChangeCount()) is { } at)
            lock (_lock)
                _wroteAt = at;
        return result;
    }

    public bool Write(Func<bool> write) => Write(write, w => w);

    // whether the user copied since at: the counter moved past it and past Hydra's own last write; null when the platform
    // keeps no counter, or kept none at
    public bool? CopiedSince(long? at) => CopiedSince(at, _sync.ChangeCount());

    // as CopiedSince(at), against now, the counter as read earlier, so a caller holding a lock reads it before taking it
    public bool? CopiedSince(long? at, long? now)
    {
        if (at == null || now == null) return null;
        long? wrote;
        lock (_lock)
            wrote = _wroteAt;
        return now != at && now != wrote;
    }

    // whether the clipboard is still Hydra's own last write, nothing copied since; never, before Hydra wrote here; null
    // when the platform keeps no counter
    public bool? StillOwnWrite()
    {
        if (_sync.ChangeCount() is not { } now) return null;
        lock (_lock)
            return _wroteAt == now;
    }
}

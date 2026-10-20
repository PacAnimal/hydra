using Microsoft.Extensions.Logging;

namespace Hydra.Platform;

// X11's primary selection, kept by a platform that has none: the newest of the selections relayed here and the
// user's text copies. The version is the OS clipboard's change counter, which only grows and says whether a copy was
// made since Hydra last touched the clipboard; every one is read under the gate, so no write can land between it and
// what it decides. readTextCopy returns the text of a text copy: null for one whose text only names an image or files
// we send instead, or for a password manager's concealed copy; it throws when the clipboard cannot be read, to be
// tried again.
public sealed class CopiedPrimary(Func<long> version, Func<string?> readTextCopy, ILogger log)
{
    private readonly Lock _gate = new();
    private string? _current;
    private long _seen = -1;
    private long _written = -1;
    private int _writing;

    public string? Read() => Guarded("read the primary selection", null, () =>
    {
        Catch(version());
        return _current;
    });

    // whether the clipboard is still what Hydra's last write left
    public bool Owns() => Guarded("check clipboard ownership", false, () => _written >= 0 && version() == _written);

    // a copy made before the selection arrived is older than it; whether it was kept
    public bool Relay(string incoming) => Guarded("keep the primary selection", false, () =>
    {
        RelayAt(incoming, version());
        return true;
    });

    // Hydra's own write of the clipboard, with the selection beside it: until it ends, what is on the clipboard is no
    // copy of the user's. write reports the version it leaves the clipboard at as soon as it knows it, even a failed
    // or partial write's, and says whether it replaced the clipboard; a failure is logged, not thrown. The gate is never
    // held across write, so a platform may take its own clipboard lock inside it
    public bool Write(string? incoming, Func<Action<long>, bool> write)
    {
        if (!Begin(incoming)) return false;
        long? left = null;
        try
        {
            return write(v => left = v);
        }
        catch (Exception ex)
        {
            log.LogWarning(ex, "Failed to write clipboard");
            return false;
        }
        finally
        {
            lock (_gate)
            {
                if (left is { } v)
                {
                    _seen = Math.Max(_seen, v); // a selection relayed during the write may have moved it on
                    _written = v;
                }
                _writing--;
            }
        }
    }

    // a copy made since Hydra last touched the clipboard is looked for before the write begins
    private bool Begin(string? incoming) => Guarded("begin writing clipboard", false, () =>
    {
        RelayAt(incoming, version());
        _writing++;
        return true;
    });

    // the version is seen once its copy was read, or a selection arrived that outranks it
    private void RelayAt(string? incoming, long at)
    {
        var read = Catch(at);
        if (incoming != null) _current = incoming;
        if (read || incoming != null) _seen = at;
    }

    // a text copy made since Hydra last touched the clipboard becomes the primary, one answer per version; false when
    // the clipboard could not be read
    private bool Catch(long at)
    {
        if (_writing > 0 || at == _seen) return true;
        string? text;
        try
        {
            text = readTextCopy();
        }
        catch (Exception ex)
        {
            if (log.IsEnabled(LogLevel.Debug))
                log.LogDebug(ex, "Clipboard unreadable for the primary selection; trying again on the next read");
            return false;
        }
        _seen = at;
        if (text != null) _current = text;
        return true;
    }

    // gated, and logged rather than thrown when the clipboard fails
    private T Guarded<T>(string what, T failed, Func<T> body)
    {
        try
        {
            lock (_gate) return body();
        }
        catch (Exception ex)
        {
            log.LogWarning(ex, "Failed to {What}", what);
            return failed;
        }
    }
}

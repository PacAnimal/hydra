namespace Hydra.Platform.Linux;

// CLIPBOARD's change counter over its owners, apart from X: each other client's taking of it counts once, whether seen
// as the current owner (Seen) or by its XFixes notice (Notice), and Hydra's own taking counts as its write (Own). An
// owner seen ahead of its notices leaves a credit, which the next notice handled spends, whatever its owner, so k takes
// count k however their notices and reads interleave. An owner gone (None) is no copy. Not thread-safe: its owner locks
internal sealed class OwnerCounter(bool notices)
{
    private nint _last;
    private int _credits;

    public long Count { get; private set; }

    // the owner as it stands, read from the server
    public void Seen(nint owner)
    {
        if (owner == _last) return;
        _last = owner;
        if (owner == nint.Zero) return;
        Count++;
        if (notices) _credits++;
    }

    // an XFixes notice that owner took CLIPBOARD, current the owner as it stands now; ours: Hydra's window, whose own
    // taking Own counted
    public void Notice(nint owner, nint current, nint ours)
    {
        if (owner == ours || current == ours) return;
        _last = current;
        if (owner == nint.Zero) return;
        if (_credits > 0) _credits--;
        else Count++;
    }

    // Hydra took CLIPBOARD: every notice still to come is older than that write, so no credit stands; the count it left
    public long Own(nint ours)
    {
        _last = ours;
        _credits = 0;
        return ++Count;
    }
}

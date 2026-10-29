namespace Hydra.Keyboard;

// The NumLock a slave had before Hydra first synced it during a visit, given back when the visit ends, so its own keypad
// works as its user left it. Synergy restores after every key; once per visit spares the lock's flicker on each repeat
// and keeps a key's press and release under one NumLock. A NumLock key the master forwards is the user's choice, kept.
// One thread only: the output handler's.
internal sealed class NumLockRestore
{
    private bool? _before;

    internal void Synced(bool had) => _before ??= had;

    internal void Toggled() => _before = null;

    internal bool? Take()
    {
        var before = _before;
        _before = null;
        return before;
    }
}

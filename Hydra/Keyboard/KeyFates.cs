namespace Hydra.Keyboard;

// Where each physical key's press went, home to the OS or away to a remote screen, so its repeats and up follow it.
// Hook/tap thread only. strayUpsPass: an up whose press was never seen passes, where swallowing one could stick a key.
internal sealed class KeyFates(bool strayUpsPass)
{
    // the physical key, and whether its press went away
    private readonly Dictionary<int, bool> _wentAway = [];

    // Returns whether to swallow the event. repeat is the platform's auto-repeat bit, or null where the record decides;
    // osHolds is whether the OS holds the key down ahead of this event, so its press went home.
    internal bool Swallow(int key, bool isUp, bool? repeat, bool osHolds, bool isVirtual)
    {
        if (osHolds) _wentAway.TryAdd(key, false);
        var recorded = _wentAway.TryGetValue(key, out var away);
        if (isUp)
        {
            _wentAway.Remove(key);
            // an up the OS holds always reaches it, or the key stays down there
            if (osHolds) return false;
            return recorded ? away : isVirtual && !strayUpsPass;
        }
        // a repeat reaches the OS only while local, and only for a press that went there
        if (repeat ?? recorded) return isVirtual || away;
        _wentAway[key] = isVirtual;
        return isVirtual;
    }

    internal void Clear() => _wentAway.Clear();
}

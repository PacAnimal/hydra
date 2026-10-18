namespace Hydra;

// a wait that doubles each time it is taken, up to a cap, until reset; not thread-safe
internal sealed class DoublingBackoff
{
    private readonly int _first;
    private readonly int _max;
    private int _next;

    internal DoublingBackoff(int first, int max)
    {
        _first = _next = first;
        _max = max;
    }

    // the wait to use now
    internal int Next()
    {
        var current = _next;
        _next = Math.Min(current * 2, _max);
        return current;
    }

    internal void Reset() => _next = _first;
}

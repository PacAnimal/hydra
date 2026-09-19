using System.Numerics;

namespace Hydra.Mouse;

// Coalesces a burst of same-kind mouse samples into one pending value while it waits to be delivered:
// an absolute sample keeps only the latest, a relative one accumulates. One slot, replaced whenever a
// different Kind arrives. Shared by the three places raw input can arrive faster than it is drained —
// the local input-processing pipeline, native output injection, and the relay send queue — so a change
// to the coalescing rule only has one implementation to change.
internal sealed class CoalescingBatch<TKind, TNum>(TKind kind, bool accumulate)
    where TNum : struct, INumber<TNum>
{
    private TNum _x;
    private TNum _y;

    public TKind Kind { get; } = kind;

    public bool Matches(TKind other) => EqualityComparer<TKind>.Default.Equals(Kind, other);

    public void Add(TNum x, TNum y)
    {
        if (accumulate) { _x += x; _y += y; }
        else { _x = x; _y = y; }
    }

    public (TNum X, TNum Y) Snapshot() => (_x, _y);
}

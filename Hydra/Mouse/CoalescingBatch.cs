using System.Numerics;

namespace Hydra.Mouse;

// Coalesces a burst of same-kind mouse samples into one pending value while it waits to be delivered:
// an absolute sample keeps only the latest, a relative one accumulates. One slot, replaced whenever a
// different Kind arrives. Shared by the local input-processing pipeline and native output injection. The
// relay send queue coalesces with its own MovementBatch, which matches on targets and clamps its sums.
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

    public CoalescingSample<TNum> Snapshot() => new(_x, _y);
}

internal readonly record struct CoalescingSample<TNum>(TNum X, TNum Y) where TNum : struct, INumber<TNum>;

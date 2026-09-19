using Hydra.Mouse;

namespace Tests.Mouse;

[TestFixture]
public class CoalescingBatchTests
{
    [Test]
    public void Absolute_KeepsOnlyLatestSample()
    {
        var batch = new CoalescingBatch<bool, int>(true, accumulate: false);

        batch.Add(1, 2);
        batch.Add(10, 20);
        batch.Add(100, 200);

        Assert.That(batch.Snapshot(), Is.EqualTo((100, 200)));
    }

    [Test]
    public void Relative_AccumulatesAllSamples()
    {
        var batch = new CoalescingBatch<bool, int>(false, accumulate: true);

        batch.Add(1, 2);
        batch.Add(10, 20);
        batch.Add(-3, 5);

        Assert.That(batch.Snapshot(), Is.EqualTo((8, 27)));
    }

    [Test]
    public void Relative_AccumulatesFloatingPointSamples()
    {
        var batch = new CoalescingBatch<int, double>(0, accumulate: true);

        batch.Add(0.5, -0.25);
        batch.Add(0.25, -0.25);

        var (x, y) = batch.Snapshot();
        using (Assert.EnterMultipleScope())
        {
            Assert.That(x, Is.EqualTo(0.75).Within(1e-9));
            Assert.That(y, Is.EqualTo(-0.5).Within(1e-9));
        }
    }

    [Test]
    public void Matches_ComparesKindByValue_NotByReference()
    {
        var batch = new CoalescingBatch<bool, int>(true, accumulate: false);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(batch.Matches(true), Is.True);
            Assert.That(batch.Matches(false), Is.False);
        }
    }

    [Test]
    public void Matches_WorksForEnumKinds()
    {
        var batch = new CoalescingBatch<Kind, int>(Kind.Delta, accumulate: true);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(batch.Matches(Kind.Delta), Is.True);
            Assert.That(batch.Matches(Kind.Absolute), Is.False);
        }
    }

    [Test]
    public void Snapshot_BeforeAnyAdd_IsZero()
    {
        var batch = new CoalescingBatch<bool, int>(true, accumulate: false);

        Assert.That(batch.Snapshot(), Is.EqualTo((0, 0)));
    }

    private enum Kind { Absolute, Delta }
}

using Hydra.Platform.Linux;

namespace Tests.Clipboard;

// X11's CLIPBOARD change counting apart from X: owners seen as they stand and XFixes notices, however they interleave
[TestFixture]
public class OwnerCounterTests
{
    private const nint Hydra = 1;
    private const nint App = 2;
    private const nint Manager = 3;

    // two takes, both notices still queued when the counter is read (seeing the second owner): two, never three
    [Test]
    public void TwoTakes_SeenAheadOfBothNotices_CountTwo()
    {
        var counter = new OwnerCounter(notices: true);
        counter.Own(Hydra);
        var before = counter.Count;

        counter.Seen(Manager);
        counter.Notice(App, Manager, Hydra);
        counter.Notice(Manager, Manager, Hydra);

        Assert.That(counter.Count, Is.EqualTo(before + 2));
    }

    // a take seen ahead of its notice counts once
    [Test]
    public void OneTake_SeenThenNoticed_CountsOnce()
    {
        var counter = new OwnerCounter(notices: true);
        counter.Own(Hydra);
        var before = counter.Count;

        counter.Seen(App);
        counter.Notice(App, App, Hydra);
        counter.Seen(App);

        Assert.That(counter.Count, Is.EqualTo(before + 1));
    }

    // a take noticed before it is seen counts once, the owner the notice left being the one seen after
    [Test]
    public void OneTake_NoticedThenSeen_CountsOnce()
    {
        var counter = new OwnerCounter(notices: true);
        counter.Own(Hydra);
        var before = counter.Count;

        counter.Notice(App, App, Hydra);
        counter.Seen(App);

        Assert.That(counter.Count, Is.EqualTo(before + 1));
    }

    // an owner taking it again (an identical re-copy) is a copy, only XFixes sees it
    [Test]
    public void SameOwnerTakesAgain_IsNoticed_AndCounts()
    {
        var counter = new OwnerCounter(notices: true);
        counter.Notice(App, App, Hydra);
        var before = counter.Count;

        counter.Notice(App, App, Hydra);

        Assert.That(counter.Count, Is.EqualTo(before + 1));
    }

    // the owning app exiting leaves no owner: no copy, and no credit left for a later notice to spend
    [TestCase(true)]
    [TestCase(false)]
    public void OwnerGone_IsNoCopy(bool notices)
    {
        var counter = new OwnerCounter(notices);
        counter.Seen(App);
        var before = counter.Count;

        counter.Seen(nint.Zero);
        var gone = counter.Count;
        counter.Seen(Manager);
        if (notices) counter.Notice(Manager, Manager, Hydra);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(gone, Is.EqualTo(before));
            Assert.That(counter.Count, Is.EqualTo(before + 1), "the next take counts once");
        }
    }

    // Hydra's own take is its write; a notice of another take older than it counts nothing, nor one of Hydra's own
    [Test]
    public void HydrasOwnTake_LeavesNoCreditAndNoNoticeOfItCounts()
    {
        var counter = new OwnerCounter(notices: true);
        counter.Seen(App);
        var wrote = counter.Own(Hydra);

        counter.Notice(App, Hydra, Hydra);
        counter.Notice(Hydra, Hydra, Hydra);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(counter.Count, Is.EqualTo(wrote));
            counter.Notice(Manager, Manager, Hydra);
            Assert.That(counter.Count, Is.EqualTo(wrote + 1), "the credit Seen left went with Hydra's write");
        }
    }

    // without XFixes only owners seen count, one per change
    [Test]
    public void WithoutNotices_EachOwnerChangeSeenCountsOnce()
    {
        var counter = new OwnerCounter(notices: false);
        counter.Own(Hydra);
        var before = counter.Count;

        counter.Seen(App);
        counter.Seen(App);
        counter.Seen(Manager);

        Assert.That(counter.Count, Is.EqualTo(before + 2));
    }
}

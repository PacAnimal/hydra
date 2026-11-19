using Hydra.Platform;
using Hydra.Relay;
using Microsoft.Extensions.Logging.Abstractions;
using Tests.Setup;

namespace Tests.Clipboard;

// The master's clipboard tells an IPullObserver of each pull it sends, and when it settles: its answer written or found
// unchanged, or the pull given up (forgotten, replaced, superseded, its stream ended or refused), never while a write is
// under way
[TestFixture]
public class PullSettledTests
{
    private sealed class Watching : IClipboardSync, IPullObserver
    {
        public FakeClipboardSync Inner { get; } = new();
        public List<Task> Sent { get; } = [];
        public ClipboardRead ReadAll() => Inner.ReadAll();
        public ClipboardSnapshot? SetClipboard(ClipboardSnapshot contents) => Inner.SetClipboard(contents);
        public long? ChangeCount() => Inner.ChangeCount();
        public long? WroteAt() => Inner.WroteAt();
        public void PullSent(Task settled) => Sent.Add(settled);
    }

    private Watching _sync = null!;
    private ClipboardMaster _master = null!;

    [SetUp]
    public void SetUp()
    {
        _sync = new Watching();
        _master = new ClipboardMaster(_sync, NullLogger.Instance);
    }

    private static ClipboardPullResponseMessage Changed(ClipboardPullMessage pull, string text) => new(pull.PullId, text);

    private static ClipboardPullResponseMessage Unchanged(ClipboardPullMessage pull) => new(pull.PullId, null, Unchanged: true);

    [Test]
    public void APull_SettlesWhenItsChangedAnswerIsWritten()
    {
        var pull = _master.Pull("slave");
        Assert.That(_sync.Sent.Single().IsCompleted, Is.False);
        _master.TakePullAnswer("slave", Changed(pull, "copied on the slave"));

        Assert.That(_sync.Sent.Single().IsCompletedSuccessfully, Is.True);
    }

    [Test]
    public void APull_SettlesWhenAnsweredUnchanged()
    {
        var pull = _master.Pull("slave");
        _master.TakePullAnswer("slave", Unchanged(pull));

        Assert.That(_sync.Sent.Single().IsCompletedSuccessfully, Is.True);
    }

    [Test]
    public void APull_SettlesWhenForgotten()
    {
        _master.Pull("slave");
        _master.ForgetPull("slave");

        Assert.That(_sync.Sent.Single().IsCompletedSuccessfully, Is.True);
    }

    [Test]
    public void APull_SettlesWhenANewerOneReplacesIt()
    {
        _master.Pull("slave");
        _master.Pull("slave");

        using (Assert.EnterMultipleScope())
        {
            Assert.That(_sync.Sent[0].IsCompletedSuccessfully, Is.True, "its answer would be ignored");
            Assert.That(_sync.Sent[1].IsCompleted, Is.False);
        }
    }

    [Test]
    public void AStreamingPull_SettlesWhenItsStreamEnds()
    {
        var pull = _master.Pull("slave");
        _master.StreamFor("slave", pull.PullId);
        Assert.That(_sync.Sent.Single().IsCompleted, Is.False, "its answer streams in");
        _master.StreamEnded("slave", pull.PullId);

        Assert.That(_sync.Sent.Single().IsCompletedSuccessfully, Is.True);
    }

    // -- a held answer --

    // a's answer to an older pull streams in and completes after newer went out, so it is held for newer's answer; newer is returned
    private ClipboardPullMessage HeldForANewerPull()
    {
        var older = _master.Pull("a");
        _master.StreamFor("a", older.PullId);
        var newer = _master.Pull("a");
        var taken = _master.TakePullAnswer("a", Changed(older, "streamed from a"));
        Assert.That(taken.HeldFor, Is.EqualTo(new OutstandingPull("a", newer.PullId)), "held");
        return newer;
    }

    [Test]
    public void AHeldAnswer_SettlesOnlyWhenItLands()
    {
        var newer = HeldForANewerPull();
        using (Assert.EnterMultipleScope())
        {
            Assert.That(_sync.Sent[0].IsCompleted, Is.False, "its answer is held, not yet written");
            Assert.That(_sync.Sent[1].IsCompleted, Is.False);
        }

        _master.TakePullAnswer("a", Unchanged(newer));

        using (Assert.EnterMultipleScope())
        {
            Assert.That(_sync.Inner.Text, Is.EqualTo("streamed from a"), "landed");
            Assert.That(_sync.Sent[0].IsCompletedSuccessfully, Is.True);
            Assert.That(_sync.Sent[1].IsCompletedSuccessfully, Is.True);
        }
    }

    [Test]
    public void AHeldAnswer_SettlesWhenItLandsAsItsPullIsWaitedOut()
    {
        var newer = HeldForANewerPull();

        _master.LandHeld(new OutstandingPull("a", newer.PullId));

        using (Assert.EnterMultipleScope())
        {
            Assert.That(_sync.Inner.Text, Is.EqualTo("streamed from a"), "landed");
            Assert.That(_sync.Sent[0].IsCompletedSuccessfully, Is.True);
            Assert.That(_sync.Sent[1].IsCompleted, Is.False, "the pull it was held for stays out");
        }
    }

    [Test]
    public void AHeldAnswer_SettlesWhenSuperseded()
    {
        HeldForANewerPull();
        var toB = _master.Pull("b");

        _master.TakePullAnswer("b", Changed(toB, "copied on b"));

        using (Assert.EnterMultipleScope())
        {
            Assert.That(_sync.Inner.Text, Is.EqualTo("copied on b"));
            Assert.That(_sync.Sent[0].IsCompletedSuccessfully, Is.True, "the held answer is dropped");
            Assert.That(_sync.Sent[1].IsCompletedSuccessfully, Is.True, "the pull it was held for is superseded");
        }
    }

    // -- a write under way --

    [Test]
    public void APullGivenUpDuringAnotherHostsWrite_SettlesOnlyOnceThatWriteIsDone()
    {
        // b's pull is the older, so its copy supersedes nothing of a's
        var toB = _master.Pull("b");
        var toA = _master.Pull("a");
        _master.StreamFor("a", toA.PullId);
        bool? settledMidWrite = null;
        _sync.Inner.AfterWrite = () =>
        {
            _sync.Inner.AfterWrite = null;
            // a's stream ends while b's answer is being written
            _master.StreamEnded("a", toA.PullId);
            settledMidWrite = _sync.Sent[1].IsCompleted;
        };

        _master.TakePullAnswer("b", Changed(toB, "copied on b"));

        using (Assert.EnterMultipleScope())
        {
            Assert.That(_sync.Inner.Text, Is.EqualTo("copied on b"));
            Assert.That(settledMidWrite, Is.False, "the write was under way");
            Assert.That(_sync.Sent[0].IsCompletedSuccessfully, Is.True);
            Assert.That(_sync.Sent[1].IsCompletedSuccessfully, Is.True);
        }
    }

    // -- a supersede, and a refused stream --

    [Test]
    public void APull_SettlesWhenANewerCopyFromAnotherHostSupersedesIt()
    {
        _master.Pull("a");
        var toB = _master.Pull("b");

        _master.TakePullAnswer("b", Changed(toB, "copied on b"));

        Assert.That(_sync.Sent[0].IsCompletedSuccessfully, Is.True);
    }

    [Test]
    public void APull_SettlesWhenAStreamingNewerCopySupersedesIt()
    {
        _master.Pull("a");
        var toB = _master.Pull("b");
        var streaming = _master.StreamFor("b", toB.PullId)!;

        _master.Streaming(streaming);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(_sync.Sent[0].IsCompletedSuccessfully, Is.True, "superseded from the Begin");
            Assert.That(_sync.Sent[1].IsCompleted, Is.False, "its answer streams in");
        }
    }

    [Test]
    public void APull_SettlesWhenItsStreamIsRefused()
    {
        _sync.Inner.SetupText("pulled against");
        var pull = _master.Pull("a");
        var answering = _master.StreamFor("a", pull.PullId)!;
        _sync.Inner.SetupText("copied on the master");

        using (Assert.EnterMultipleScope())
        {
            Assert.That(_master.RefuseStream(answering), Is.True);
            Assert.That(_sync.Sent.Single().IsCompletedSuccessfully, Is.True);
        }
    }
}

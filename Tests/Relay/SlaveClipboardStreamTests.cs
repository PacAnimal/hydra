using Cathedral.Extensions;
using Hydra.Platform;
using Hydra.Relay;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Time.Testing;
using Tests.Setup;
using static Tests.Setup.ClipboardStreaming;

namespace Tests.Relay;

// the slave's side of clipboard streaming: pushes the master streams in, pull answers the slave streams out
[TestFixture]
public class SlaveClipboardStreamTests
{
    private readonly SlaveRelays _slaves = new();
    private FakeClipboardSync _clipboard = null!;
    private RecordingOsd _osd = null!;
    private FakeTimeProvider _clock = null!;
    private CapturingLog<RelayConnection> _log = null!;

    [SetUp]
    public void SetUp()
    {
        _clipboard = new FakeClipboardSync();
        _osd = new RecordingOsd();
        _clock = new FakeTimeProvider();
        _log = new CapturingLog<RelayConnection>(LogLevel.Warning);
    }

    [TearDown]
    public Task TearDown() => _slaves.StopAll();

    private TestableSlaveRelay Slave() => _slaves.New(_clipboard, _osd, _clock, _log);

    // -- the clipboard worker --

    // a clipboard read hung on its owner holds up only the clipboard: a key behind it is applied at once, and the
    // clipboard work completes in order once the read returns
    [Test]
    public async Task ReadHung_AKeyAfterAHashAndAPullIsStillApplied()
    {
        _clipboard.SetupText("on the slave");
        var slave = Slave();
        _clipboard.ReadGate.Hold();
        try
        {
            await slave.Deliver(Master, MessageKind.ClipboardHash, new ClipboardHashMessage(ContentHash).ToSaneJson());
            await _clipboard.ReadsParked.Next("the hash's read to park");
            await slave.Deliver(Master, MessageKind.ClipboardPull, new ClipboardPullMessage(1).ToSaneJson());
            await slave.Deliver(Master, MessageKind.KeyEvent, """{"type":0,"modifiers":0,"key":22}""");
            var whileHeld = new { Keys = slave.Output.Keys.Count, Sent = slave.Sent.Snapshot().Select(s => s.Kind).ToList() };

            _clipboard.ReadGate.Release();
            await slave.ClipboardIdle();
            var sent = slave.Sent.Snapshot().Select(s => s.Kind).Where(k => k is MessageKind.ClipboardPullRequest or MessageKind.ClipboardPullResponse);

            using (Assert.EnterMultipleScope())
            {
                Assert.That(whileHeld.Keys, Is.EqualTo(1), "the key is applied while the read is held");
                Assert.That(whileHeld.Sent, Is.Empty, "the clipboard work waits on the read");
                Assert.That(sent, Is.EqualTo([MessageKind.ClipboardPullRequest, MessageKind.ClipboardPullResponse]), "the hash's work, then the pull's");
            }
        }
        finally
        {
            _clipboard.ReadGate.Release();
        }
    }

    // -- the master's streamed pushes --

    [Test]
    public async Task StreamedPush_EmptiesAtBeginAndAppliesAfterTheLastChunk()
    {
        _clipboard.SetupText("old on the slave");
        var slave = Slave();
        var text = BigText('a');
        var stream = StreamedPush(1, new ClipboardPushMessage(text));

        await Begin(slave, stream);
        var emptied = new { _clipboard.ClearClipboardCallCount, _clipboard.Text };
        await Chunks(slave, stream);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(emptied.ClearClipboardCallCount, Is.EqualTo(1));
            Assert.That(emptied.Text, Is.Null);
            Assert.That(_clipboard.Text, Is.EqualTo(text));
        }
    }

    [Test]
    public async Task PullMidStream_ForTheStreamsHash_IsAnsweredUnchanged()
    {
        var slave = Slave();
        var stream = StreamedPush(1, new ClipboardPushMessage(BigText('a')));
        await Begin(slave, stream);
        await Chunk(slave, stream.Chunks[0]);

        await Pull(slave, 7, ContentHash);

        var answer = slave.Sent.Of<ClipboardPullResponseMessage>(MessageKind.ClipboardPullResponse, Master).Single();
        using (Assert.EnterMultipleScope())
        {
            Assert.That(answer.PullId, Is.EqualTo(7));
            Assert.That(answer.Unchanged, Is.True);
            Assert.That(answer.Text, Is.Null);
            Assert.That(answer.ImagePng, Is.Null);
        }
    }

    [Test]
    public async Task HashMidStream_ForTheStreamsHash_AsksForNothing()
    {
        var slave = Slave();
        var stream = StreamedPush(1, new ClipboardPushMessage(BigText('a')));
        await Begin(slave, stream);
        await Chunk(slave, stream.Chunks[0]);

        await slave.SimulateReceive(Master, MessageKind.ClipboardHash, new ClipboardHashMessage(ContentHash).ToSaneJson());

        Assert.That(slave.Sent.Of(MessageKind.ClipboardPullRequest), Is.Empty);
    }

    // the hash emptied the clipboard for a push that will not come; the master says so, and that is told
    [Test]
    public async Task HashDiffers_TheMasterHasNoPush_TheFailureIsToldAndTheClipboardStaysEmpty()
    {
        _clipboard.SetupText("old here");
        var slave = Slave();
        await slave.SimulateReceive(Master, MessageKind.ClipboardHash, new ClipboardHashMessage(ContentHash, Visit: 3).ToSaneJson());
        var request = slave.Sent.Of<ClipboardPullRequestMessage>(MessageKind.ClipboardPullRequest, Master).Single();

        await slave.SimulateReceive(Master, MessageKind.ClipboardPushNone, new ClipboardPushNoneMessage(3).ToSaneJson());

        using (Assert.EnterMultipleScope())
        {
            Assert.That(request.Visit, Is.EqualTo(3), "the request names the hash's visit");
            Assert.That(await _osd.Shown.Next("the failure"), Is.EqualTo(ClipboardStreamReceiver.FailedOsd));
            Assert.That(_log.Entries.Any(e => e is { Level: LogLevel.Warning } && e.Message.Contains("will not come")), Is.True);
            Assert.That(_clipboard.Text, Is.Null, "an honest empty");
            Assert.That(_clipboard.SetClipboardCallCount, Is.Zero);
        }
    }

    [Test]
    public async Task NoPushForAnOlderVisit_IsNotTold()
    {
        _clipboard.SetupText("old here");
        var slave = Slave();
        await slave.SimulateReceive(Master, MessageKind.ClipboardHash, new ClipboardHashMessage(ContentHash, Visit: 4).ToSaneJson());

        await slave.SimulateReceive(Master, MessageKind.ClipboardPushNone, new ClipboardPushNoneMessage(3).ToSaneJson());

        Assert.That(_osd.Shown.Count, Is.Zero);
    }

    // an owed read finding nothing names the host's latest visit, newer than the hash's when the cursor came back since
    [Test]
    public async Task NoPushForANewerVisit_TheFailureIsTold()
    {
        _clipboard.SetupText("old here");
        var slave = Slave();
        await slave.SimulateReceive(Master, MessageKind.ClipboardHash, new ClipboardHashMessage(ContentHash, Visit: 3).ToSaneJson());

        await slave.SimulateReceive(Master, MessageKind.ClipboardPushNone, new ClipboardPushNoneMessage(4).ToSaneJson());

        using (Assert.EnterMultipleScope())
        {
            Assert.That(await _osd.Shown.Next("the failure"), Is.EqualTo(ClipboardStreamReceiver.FailedOsd));
            Assert.That(_log.Entries.Any(e => e is { Level: LogLevel.Warning } && e.Message.Contains("will not come")), Is.True);
        }
    }

    [Test]
    public async Task NoPushWithoutAVisit_ForAHashWithOne_IsNotTold()
    {
        _clipboard.SetupText("old here");
        var slave = Slave();
        await slave.SimulateReceive(Master, MessageKind.ClipboardHash, new ClipboardHashMessage(ContentHash, Visit: 3).ToSaneJson());

        await slave.SimulateReceive(Master, MessageKind.ClipboardPushNone, new ClipboardPushNoneMessage(null).ToSaneJson());

        Assert.That(_osd.Shown.Count, Is.Zero);
    }

    [Test]
    public async Task UserCopiesMidStream_AbortsItAndKeepsTheCopy()
    {
        var slave = Slave();
        var stream = StreamedPush(5, new ClipboardPushMessage(BigText('a')));
        await Begin(slave, stream);
        await Chunk(slave, stream.Chunks[0]);

        _clipboard.CanUseEchoFallbackValue = false;
        _clipboard.SetupText("the user's copy");
        await Chunks(slave, stream, from: 1);
        await Pull(slave, 8, ContentHash);

        var answer = slave.Sent.Of<ClipboardPullResponseMessage>(MessageKind.ClipboardPullResponse, Master).Single();
        using (Assert.EnterMultipleScope())
        {
            Assert.That(AbortsTo(slave.Sent, Master), Is.EqualTo([5L]));
            Assert.That(_clipboard.SetClipboardCallCount, Is.Zero);
            Assert.That(_clipboard.Text, Is.EqualTo("the user's copy"));
            Assert.That(answer.Unchanged, Is.Null);
            Assert.That(answer.Text, Is.EqualTo("the user's copy"));
        }
    }

    [Test]
    public async Task UserCopiesAfterAPendingHash_TheRealHashAsksForNothing_AndAPullAnswersTheCopy()
    {
        _clipboard.SetupText("old");
        var slave = Slave();
        await slave.SimulateReceive(Master, MessageKind.ClipboardHash, new ClipboardHashMessage(null, Pending: true).ToSaneJson());

        _clipboard.SetupText("the user's copy");
        _clipboard.CanUseEchoFallbackValue = false;
        var landed = ClipboardWire.ToHash(new ClipboardSnapshot(BigText('a'), null, null));
        await slave.SimulateReceive(Master, MessageKind.ClipboardHash, landed.ToSaneJson());
        await Pull(slave, 9, landed.Hash);

        var answer = slave.Sent.Of<ClipboardPullResponseMessage>(MessageKind.ClipboardPullResponse, Master).Single();
        using (Assert.EnterMultipleScope())
        {
            Assert.That(slave.Sent.Of(MessageKind.ClipboardPullRequest), Is.Empty);
            Assert.That(_clipboard.Text, Is.EqualTo("the user's copy"));
            Assert.That(answer.Unchanged, Is.Null);
            Assert.That(answer.Text, Is.EqualTo("the user's copy"));
        }
    }

    // the hash emptied the clipboard for a push; the user copies here before it comes, and that copy is newer
    [Test]
    public async Task UserCopiesAfterTheHashEmptied_TheStreamsBegin_IsAbortedAndTheCopyKept()
    {
        _clipboard.SetupText("old here");
        var slave = Slave();
        await slave.SimulateReceive(Master, MessageKind.ClipboardHash, new ClipboardHashMessage(ContentHash, Visit: 3).ToSaneJson());
        _clipboard.SetupText("the user's copy");
        _clipboard.CanUseEchoFallbackValue = false;
        var stream = StreamedPush(4, new ClipboardPushMessage(BigText('a')));

        await Begin(slave, stream);
        await Chunks(slave, stream);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(slave.Sent.Of<ClipboardStreamAbortMessage>(MessageKind.ClipboardStreamAbort, Master), Is.EqualTo([new ClipboardStreamAbortMessage(4)]));
            Assert.That(_clipboard.ClearClipboardCallCount, Is.EqualTo(1), "only the hash emptied it");
            Assert.That(_clipboard.Text, Is.EqualTo("the user's copy"));
            Assert.That(_osd.Shown.Count, Is.Zero);
        }
    }

    [Test]
    public async Task UserCopiesAfterTheHashEmptied_TheWholePush_IsIgnored()
    {
        _clipboard.SetupText("old here");
        var slave = Slave();
        await slave.SimulateReceive(Master, MessageKind.ClipboardHash, new ClipboardHashMessage(ContentHash, Visit: 3).ToSaneJson());
        _clipboard.SetupText("the user's copy");
        _clipboard.CanUseEchoFallbackValue = false;

        await slave.SimulateReceive(Master, MessageKind.ClipboardPush, new ClipboardPushMessage("the master's").ToSaneJson());

        using (Assert.EnterMultipleScope())
        {
            Assert.That(_clipboard.SetClipboardCallCount, Is.Zero);
            Assert.That(_clipboard.Text, Is.EqualTo("the user's copy"));
        }
    }

    [Test]
    public async Task StillEmptiedByTheHash_TheStreamsBegin_IsTaken()
    {
        _clipboard.SetupText("old here");
        var slave = Slave();
        await slave.SimulateReceive(Master, MessageKind.ClipboardHash, new ClipboardHashMessage(ContentHash, Visit: 3).ToSaneJson());
        var text = BigText('a');
        var stream = StreamedPush(4, new ClipboardPushMessage(text));

        await Begin(slave, stream);
        await Chunks(slave, stream);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(slave.Sent.Of(MessageKind.ClipboardStreamAbort), Is.Empty);
            Assert.That(_clipboard.Text, Is.EqualTo(text));
        }
    }

    [Test]
    public async Task ChunkOfAnOlderStreamAfterANewBegin_IsIgnored()
    {
        var slave = Slave();
        var old = StreamedPush(1, new ClipboardPushMessage(BigText('a')));
        var fresh = StreamedPush(2, new ClipboardPushMessage(BigText('b')));
        await Begin(slave, old);
        await Chunk(slave, old.Chunks[0]);

        await Begin(slave, fresh);
        await Chunks(slave, old, from: 1);
        var setByOld = _clipboard.SetClipboardCallCount;
        await Chunks(slave, fresh);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(setByOld, Is.Zero);
            Assert.That(_clipboard.Text, Is.EqualTo(BigText('b')));
            Assert.That(_osd.Shown.Count, Is.Zero);
        }
    }

    [TestCase(false)]
    [TestCase(true)]
    public async Task MasterLeavesMidStream_ItsStreamIsDropped_AndTheFailureIsToldOnce(bool disconnect)
    {
        var slave = Slave();
        var stream = StreamedPush(1, new ClipboardPushMessage(BigText('a')));
        await Begin(slave, stream);
        await Chunk(slave, stream.Chunks[0]);

        await Depart(slave, disconnect);
        await Chunks(slave, stream, from: 1);
        // a stream still pending would time out here, telling it a second time
        _clock.Advance(ClipboardStreamReceiver.Watchdog);

        // the push this slave waited on never lands: a failure, logged naming the master and shown here
        var warning = await _log.WaitFor(e => e.Level == LogLevel.Warning && e.Message.Contains("lipboard"));
        using (Assert.EnterMultipleScope())
        {
            Assert.That(_clipboard.SetClipboardCallCount, Is.Zero);
            Assert.That(_clipboard.Text, Is.Null);
            Assert.That(warning.Message, Does.Contain(Master));
            Assert.That(await _osd.Shown.Next("the failure"), Is.EqualTo(ClipboardStreamReceiver.FailedOsd));
            Assert.That(_osd.Shown.Count, Is.Zero, "the failure is shown once");
        }
    }

    [Test]
    public async Task StreamedPush_BadBlobHash_LogsTheFailure_AndShowsIt()
    {
        var slave = Slave();
        var stream = StreamedPush(1, new ClipboardPushMessage(BigText('a')), blobHash: 12345);

        await Begin(slave, stream);
        await Chunks(slave, stream);

        var warning = await _log.WaitFor(e => e.Level == LogLevel.Warning);
        using (Assert.EnterMultipleScope())
        {
            Assert.That(warning.Message, Does.Contain(Master).And.Contain("don't match"));
            Assert.That(await _osd.Shown.Next("the failure"), Is.EqualTo(ClipboardStreamReceiver.FailedOsd));
            Assert.That(_clipboard.SetClipboardCallCount, Is.Zero);
            Assert.That(_clipboard.Text, Is.Null);
        }
    }

    [Test]
    public async Task StreamedPush_NoChunkWithinTheWatchdog_LogsTheFailure_AndShowsIt()
    {
        var slave = Slave();
        var stream = StreamedPush(1, new ClipboardPushMessage(BigText('a')));
        await Begin(slave, stream);
        await Chunk(slave, stream.Chunks[0]);

        _clock.Advance(ClipboardStreamReceiver.Watchdog);

        var warning = await _log.WaitFor(e => e.Level == LogLevel.Warning);
        using (Assert.EnterMultipleScope())
        {
            Assert.That(warning.Message, Does.Contain(Master).And.Contain("timed out"));
            Assert.That(await _osd.Shown.Next("the failure"), Is.EqualTo(ClipboardStreamReceiver.FailedOsd));
            Assert.That(_clipboard.Text, Is.Null);
        }
    }

    // a user's copy is newer than anything a broken Begin says is coming, so it stays; still a failure
    [TestCase(MessageKind.ClipboardPush, 0)]
    [TestCase(MessageKind.ClipboardPullResponse, 1024 * 1024)]
    public async Task StreamBegin_Rejected_KeepsTheUsersCopy_LogsAndShowsTheFailure_AndAbortsTheMaster(MessageKind inner, int totalBytes)
    {
        _clipboard.SetupText("old on the slave");
        var slave = Slave();

        await slave.SimulateReceive(Master, MessageKind.ClipboardStreamBegin,
            new ClipboardStreamBeginMessage(6, inner, totalBytes, 0, ContentHash).ToSaneJson());

        var warning = await _log.WaitFor(e => e.Level == LogLevel.Warning);
        using (Assert.EnterMultipleScope())
        {
            Assert.That(warning.Message, Does.Contain(Master).And.Contain("Rejected"));
            Assert.That(await _osd.Shown.Next("the failure"), Is.EqualTo(ClipboardStreamReceiver.FailedOsd));
            Assert.That(_clipboard.ClearClipboardCallCount, Is.Zero);
            Assert.That(_clipboard.Text, Is.EqualTo("old on the slave"));
            Assert.That(AbortsTo(slave.Sent, Master), Is.EqualTo([6L]));
        }
    }

    // still told a newer clipboard is coming, so Hydra's old write never stays pasteable
    [TestCase(MessageKind.ClipboardPush, 0)]
    [TestCase(MessageKind.ClipboardPullResponse, 1024 * 1024)]
    public async Task StreamBegin_Rejected_EmptiesHydrasWrite_AndShowsTheFailure(MessageKind inner, int totalBytes)
    {
        var slave = Slave();
        await slave.SimulateReceive(Master, MessageKind.ClipboardPush, new ClipboardPushMessage("from the master").ToSaneJson());

        await slave.SimulateReceive(Master, MessageKind.ClipboardStreamBegin,
            new ClipboardStreamBeginMessage(6, inner, totalBytes, 0, ContentHash).ToSaneJson());

        using (Assert.EnterMultipleScope())
        {
            Assert.That(await _osd.Shown.Next("the failure"), Is.EqualTo(ClipboardStreamReceiver.FailedOsd));
            Assert.That(_clipboard.ClearClipboardCallCount, Is.EqualTo(1));
            Assert.That(_clipboard.Text, Is.Null);
        }
    }

    [Test]
    public async Task StreamBegin_Rejected_DropsTheOlderStream()
    {
        var slave = Slave();
        var old = StreamedPush(1, new ClipboardPushMessage(BigText('a')));
        await Begin(slave, old);
        await Chunk(slave, old.Chunks[0]);

        await slave.SimulateReceive(Master, MessageKind.ClipboardStreamBegin, (old.Begin with { Id = 2, TotalBytes = 0 }).ToSaneJson());
        await _osd.Shown.Next("the failure");
        await Chunks(slave, old, from: 1);
        _clock.Advance(ClipboardStreamReceiver.Watchdog * 2);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(_clipboard.SetClipboardCallCount, Is.Zero);
            Assert.That(_clipboard.Text, Is.Null);
            Assert.That(_osd.Shown.Count, Is.Zero, "the failure is shown once");
            Assert.That(AbortsTo(slave.Sent, Master), Is.EqualTo([2L]));
        }
    }

    [Test]
    public async Task StreamBegin_Unparseable_IsLoggedAndDropped_LikeAnyMessage()
    {
        var slave = Slave();
        await slave.SimulateReceive(Master, MessageKind.ClipboardPush, new ClipboardPushMessage("from the master").ToSaneJson());

        await slave.SimulateReceive(Master, MessageKind.ClipboardStreamBegin, "not json");

        var warning = await _log.WaitFor(e => e.Level == LogLevel.Warning);
        using (Assert.EnterMultipleScope())
        {
            Assert.That(warning.Message, Does.Contain(nameof(ClipboardStreamBeginMessage)).And.Contain(Master));
            Assert.That(_clipboard.Text, Is.EqualTo("from the master"));
            Assert.That(slave.Sent.Of(MessageKind.ClipboardStreamAbort), Is.Empty);
        }
    }

    [Test]
    public async Task StreamedPush_BadBlobHash_AbortsTheMaster()
    {
        var slave = Slave();
        var stream = StreamedPush(3, new ClipboardPushMessage(BigText('a')), blobHash: 12345);

        await Begin(slave, stream);
        await Chunks(slave, stream);

        Assert.That(AbortsTo(slave.Sent, Master), Is.EqualTo([3L]));
    }

    [Test]
    public async Task StreamedPush_NoChunkWithinTheWatchdog_AbortsTheMaster()
    {
        var slave = Slave();
        var stream = StreamedPush(3, new ClipboardPushMessage(BigText('a')));
        await Begin(slave, stream);
        await Chunk(slave, stream.Chunks[0]);

        _clock.Advance(ClipboardStreamReceiver.Watchdog);

        await slave.Sent.WaitFor(MessageKind.ClipboardStreamAbort, 1, Master);
        Assert.That(AbortsTo(slave.Sent, Master), Is.EqualTo([3L]));
    }

    [Test]
    public async Task StreamedPush_AnEmptyChunk_FailsAndAbortsTheMaster()
    {
        var slave = Slave();
        var stream = StreamedPush(3, new ClipboardPushMessage(BigText('a')));
        await Begin(slave, stream);

        await Chunk(slave, stream.Chunks[0] with { Data = [] });
        await Chunks(slave, stream);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(await _osd.Shown.Next("the failure"), Is.EqualTo(ClipboardStreamReceiver.FailedOsd));
            Assert.That(_clipboard.SetClipboardCallCount, Is.Zero);
            Assert.That(AbortsTo(slave.Sent, Master), Is.EqualTo([3L]));
        }
    }

    [Test]
    public async Task StreamedPush_CompleteButUnparseable_ShowsTheFailure_AndAbortsTheMaster()
    {
        var slave = Slave();
        byte[] blob = [(byte)MessageKind.ClipboardPush, .. "not json"u8.ToArray(), .. new byte[ClipboardStreamSender.Threshold]];
        var stream = StreamedClipboard.Split(3, blob, ClipboardStreamSender.ChunkBytes, MessageKind.ClipboardPush, ContentHash);

        await Begin(slave, stream);
        await Chunks(slave, stream);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(await _osd.Shown.Next("the failure"), Is.EqualTo(ClipboardStreamReceiver.FailedOsd));
            Assert.That(_clipboard.SetClipboardCallCount, Is.Zero);
            Assert.That(_clipboard.Text, Is.Null);
            Assert.That(AbortsTo(slave.Sent, Master), Is.EqualTo([3L]));
        }
    }

    [Test]
    public async Task StreamedPush_AbortedThenANewStream_TheNewContentLandsAndTheOldChunksApplyNothing()
    {
        var slave = Slave();
        var old = StreamedPush(4, new ClipboardPushMessage(BigText('a')));
        var fresh = StreamedPush(5, new ClipboardPushMessage(BigText('b')));
        await Begin(slave, old);
        await Chunk(slave, old.Chunks[0]);

        await Abort(slave, 4);
        await Begin(slave, fresh);
        await Chunks(slave, old, from: 1);
        var setByOld = _clipboard.SetClipboardCallCount;
        await Chunks(slave, fresh);
        await Chunks(slave, old, from: 1);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(setByOld, Is.Zero);
            Assert.That(_clipboard.SetClipboardCallCount, Is.EqualTo(1));
            Assert.That(_clipboard.Text, Is.EqualTo(BigText('b')));
            Assert.That(_osd.Shown.Count, Is.Zero);
        }
    }

    [Test]
    public async Task StreamedPush_AbortedThenAWholePush_TheNewContentLandsAndTheOldChunksApplyNothing()
    {
        var slave = Slave();
        var old = StreamedPush(4, new ClipboardPushMessage(BigText('a')));
        await Begin(slave, old);
        await Chunk(slave, old.Chunks[0]);

        await Abort(slave, 4);
        await slave.SimulateReceive(Master, MessageKind.ClipboardPush, new ClipboardPushMessage("the new copy").ToSaneJson());
        var setByWhole = _clipboard.SetClipboardCallCount;
        await Chunks(slave, old, from: 1);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(_clipboard.Text, Is.EqualTo("the new copy"));
            Assert.That(_clipboard.SetClipboardCallCount, Is.EqualTo(setByWhole));
            Assert.That(_osd.Shown.Count, Is.Zero);
        }
    }

    [Test]
    public async Task StreamedPush_TheMasterGaveUp_ShowsTheFailureAndAppliesNothing()
    {
        var slave = Slave();
        var stream = StreamedPush(4, new ClipboardPushMessage(BigText('a')));
        await Begin(slave, stream);
        await Chunk(slave, stream.Chunks[0]);

        await slave.SimulateReceive(Master, MessageKind.ClipboardStreamAbort, new ClipboardStreamAbortMessage(4, Failed: true).ToSaneJson());
        await Chunks(slave, stream, from: 1);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(await _osd.Shown.Next("the failure"), Is.EqualTo(ClipboardStreamReceiver.FailedOsd));
            Assert.That(_osd.Shown.Count, Is.Zero, "the failure is shown once");
            Assert.That(_log.Entries.Select(e => e.Message), Has.Some.Contains(Master).And.Contains("gave up"));
            Assert.That(_clipboard.SetClipboardCallCount, Is.Zero);
            Assert.That(slave.Sent.Of(MessageKind.ClipboardStreamAbort), Is.Empty, "the master already dropped it");
        }
    }

    [Test]
    public async Task StreamedPush_AbortedByTheMaster_ShowsNothingAndAppliesNothing()
    {
        var slave = Slave();
        var stream = StreamedPush(4, new ClipboardPushMessage(BigText('a')));
        await Begin(slave, stream);
        await Chunk(slave, stream.Chunks[0]);

        await Abort(slave, 4);
        await Chunks(slave, stream, from: 1);
        // a stream still pending would time out here
        _clock.Advance(ClipboardStreamReceiver.Watchdog);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(_osd.Shown.Count, Is.Zero);
            Assert.That(_clipboard.SetClipboardCallCount, Is.Zero);
            Assert.That(_clipboard.Text, Is.Null);
        }
    }

    [Test]
    public async Task WholePushWhileAStreamIsPending_IsAppliedAndTheStreamDropped()
    {
        var slave = Slave();
        var stream = StreamedPush(1, new ClipboardPushMessage(BigText('a')));
        await Begin(slave, stream);
        await Chunk(slave, stream.Chunks[0]);

        await slave.SimulateReceive(Master, MessageKind.ClipboardPush, new ClipboardPushMessage("whole").ToSaneJson());
        var setAfterWhole = _clipboard.SetClipboardCallCount;
        await Chunks(slave, stream, from: 1);
        // a stream still pending would time out here
        _clock.Advance(ClipboardStreamReceiver.Watchdog);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(_clipboard.Text, Is.EqualTo("whole"));
            Assert.That(_clipboard.SetClipboardCallCount, Is.EqualTo(setAfterWhole));
            Assert.That(_osd.Shown.Count, Is.Zero);
        }
    }

    // -- dormancy --

    [Test]
    public async Task Dormant_StreamBegin_IsRefused()
    {
        var slave = Slave();
        _clipboard.SetupText("kept");
        await slave.Dormancy.Enter();
        var stream = StreamedPush(1, new ClipboardPushMessage(BigText('a')));

        await Begin(slave, stream);
        await Chunks(slave, stream);
        _clock.Advance(ClipboardStreamReceiver.Watchdog);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(_clipboard.ClearClipboardCallCount, Is.Zero);
            Assert.That(_clipboard.SetClipboardCallCount, Is.Zero);
            Assert.That(_clipboard.Text, Is.EqualTo("kept"));
            Assert.That(_osd.Shown.Count, Is.Zero);
            Assert.That(slave.Sent.Of<ClipboardStreamAbortMessage>(MessageKind.ClipboardStreamAbort, Master),
                Is.EqualTo([new ClipboardStreamAbortMessage(1, Failed: true)]), "the master stops streaming");
        }
    }

    // a pull refused while dormant is answered unchanged (the user is away, nothing was copied), so the master's pull and
    // any answer held for it resolve
    [Test]
    public async Task Dormant_Pull_IsAnsweredUnchanged()
    {
        _clipboard.SetupText("copied here");
        var slave = Slave();
        await slave.Dormancy.Enter();
        var reads = _clipboard.ReadCallCount;

        await Pull(slave, 7, masterHash: TextHash("on the master"));

        using (Assert.EnterMultipleScope())
        {
            Assert.That(slave.Sent.Of<ClipboardPullResponseMessage>(MessageKind.ClipboardPullResponse, Master),
                Is.EqualTo([new ClipboardPullResponseMessage(7, null, Unchanged: true)]));
            Assert.That(_clipboard.ReadCallCount, Is.EqualTo(reads), "nothing read on a dark screen");
        }
    }

    // queued behind a hung read as the displays go dormant: refused on the worker, still answered unchanged
    [Test]
    public async Task PullQueuedAsDormancyStarts_IsAnsweredUnchanged()
    {
        _clipboard.SetupText("kept");
        var slave = Slave();
        _clipboard.ReadGate.Hold();
        Task entering;
        try
        {
            await slave.Deliver(Master, MessageKind.ClipboardHash, new ClipboardHashMessage(TextHash("kept")).ToSaneJson());
            await _clipboard.ReadsParked.Next("the hash's read to park");
            await slave.Deliver(Master, MessageKind.ClipboardPull, new ClipboardPullMessage(7, TextHash("on the master")).ToSaneJson());
            entering = slave.Dormancy.Enter();
        }
        finally
        {
            _clipboard.ReadGate.Release();
        }
        await entering;
        await slave.ClipboardIdle();

        Assert.That(slave.Sent.Of<ClipboardPullResponseMessage>(MessageKind.ClipboardPullResponse, Master),
            Is.EqualTo([new ClipboardPullResponseMessage(7, null, Unchanged: true)]));
    }

    // queued behind a hung read as the displays go dormant, after dormancy dropped every stream: refused as a dormant
    // slave refuses it, a Begin told failed and nothing emptied, nor a push awaited
    [TestCase(MessageKind.ClipboardStreamBegin)]
    [TestCase(MessageKind.ClipboardHash)]
    public async Task QueuedAsDormancyStarts_ItIsRefusedAsWhileDormant(MessageKind kind)
    {
        _clipboard.SetupText("kept");
        var slave = Slave();
        var stream = StreamedPush(1, new ClipboardPushMessage(BigText('a')));
        _clipboard.ReadGate.Hold();
        Task entering;
        try
        {
            await slave.Deliver(Master, MessageKind.ClipboardHash, new ClipboardHashMessage(TextHash("kept")).ToSaneJson());
            await _clipboard.ReadsParked.Next("the first hash's read to park");
            if (kind == MessageKind.ClipboardStreamBegin) await slave.Deliver(Master, kind, stream.Begin.ToSaneJson());
            else await slave.Deliver(Master, kind, new ClipboardHashMessage(TextHash("on the master")).ToSaneJson());
            entering = slave.Dormancy.Enter();
        }
        finally
        {
            _clipboard.ReadGate.Release();
        }
        await entering;
        await slave.ClipboardIdle();
        _clock.Advance(ClipboardStreamReceiver.Watchdog);
        await slave.Dormancy.Exit();
        await slave.SimulateDisconnected();

        using (Assert.EnterMultipleScope())
        {
            Assert.That(slave.Dormancy.IsDormant, Is.False, "pre-condition: woke");
            Assert.That(_clipboard.ClearClipboardCallCount, Is.Zero, "nothing emptied on a dark screen");
            Assert.That(_clipboard.Text, Is.EqualTo("kept"));
            Assert.That(slave.Sent.Of(MessageKind.ClipboardPullRequest), Is.Empty, "no push asked for");
            Assert.That(_osd.Shown.Count, Is.Zero, "nothing pending, nor awaited, to fail");
            Assert.That(slave.Sent.Of<ClipboardStreamAbortMessage>(MessageKind.ClipboardStreamAbort, Master),
                kind == MessageKind.ClipboardStreamBegin ? Is.EqualTo([new ClipboardStreamAbortMessage(1, Failed: true)]) : Is.Empty);
        }
    }

    [Test]
    public async Task GoingDormantMidStream_ItsNextChunkDropsIt()
    {
        var slave = Slave();
        var stream = StreamedPush(1, new ClipboardPushMessage(BigText('a')));
        await Begin(slave, stream);
        await Chunk(slave, stream.Chunks[0]);

        await slave.Dormancy.Enter();
        await Chunk(slave, stream.Chunks[1]);
        await slave.Dormancy.Exit();
        await Chunks(slave, stream, from: 2);
        _clock.Advance(ClipboardStreamReceiver.Watchdog * 2);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(_clipboard.SetClipboardCallCount, Is.Zero);
            Assert.That(_clipboard.Text, Is.Null);
            Assert.That(_osd.Shown.Count, Is.Zero);
        }
    }

    // -- the slave's pull answers --

    [Test]
    public async Task SmallClipboard_Pull_AnswersInOnePiece()
    {
        _clipboard.SetupText("small");
        var slave = Slave();

        await Pull(slave);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(slave.Sent.Of<ClipboardPullResponseMessage>(MessageKind.ClipboardPullResponse, Master).Select(a => a.Text), Is.EqualTo(["small"]));
            Assert.That(slave.Sent.Of(MessageKind.ClipboardStreamBegin), Is.Empty);
        }
    }

    [Test]
    public async Task BigClipboard_Pull_StreamsTheAnswerToTheMaster()
    {
        var image = BigImage(1);
        _clipboard.SetupImage(image);
        var slave = Slave();

        await Pull(slave);
        var answer = await SentAnswer(slave);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(answer.PullId, Is.EqualTo(1));
            Assert.That(answer.ImagePng, Is.EqualTo(image));
            Assert.That(slave.Sent.Of(MessageKind.ClipboardPullResponse), Is.Empty);
            Assert.That(slave.Sent.TargetsOf(MessageKind.ClipboardStreamBegin), Is.EqualTo([Master]));
            Assert.That(slave.Sent.TargetsOf(MessageKind.ClipboardStreamChunk), Is.All.EqualTo(Master));
        }
    }

    [TestCase(false)]
    [TestCase(true)]
    public async Task MasterLeavesMidAnswer_NoFurtherChunksGoToIt(bool disconnect)
    {
        _clipboard.SetupImage(BigImage(1));
        var slave = Slave();
        slave.Reliable.Hold();
        await Pull(slave);
        await slave.Sent.WaitFor(MessageKind.ClipboardStreamBegin, 1, Master);
        var dropped = slave.Sent.Of<ClipboardStreamBeginMessage>(MessageKind.ClipboardStreamBegin).Single().Id;

        await Depart(slave, disconnect);
        slave.Reliable.Release();

        // a fresh answer runs start to end on the same loop, so the dropped stream's step has long passed
        await Pull(slave, 2);
        await slave.Sent.WaitFor(MessageKind.ClipboardStreamBegin, 2, Master);
        await SentAnswer(slave);
        Assert.That(slave.Sent.Of<ClipboardStreamChunkMessage>(MessageKind.ClipboardStreamChunk).Select(c => c.Id), Has.None.EqualTo(dropped));
    }

    [Test]
    public async Task MasterAbortsTheAnswer_NoFurtherChunksGoToIt()
    {
        _clipboard.SetupImage(BigImage(1));
        var slave = Slave();
        slave.Reliable.Hold();
        await Pull(slave);
        await slave.Sent.WaitFor(MessageKind.ClipboardStreamBegin, 1, Master);
        var dropped = slave.Sent.Of<ClipboardStreamBeginMessage>(MessageKind.ClipboardStreamBegin).Single().Id;

        await Abort(slave, dropped);
        slave.Reliable.Release();

        await Pull(slave, 2);
        await slave.Sent.WaitFor(MessageKind.ClipboardStreamBegin, 2, Master);
        await SentAnswer(slave);
        using (Assert.EnterMultipleScope())
        {
            Assert.That(slave.Sent.Of<ClipboardStreamChunkMessage>(MessageKind.ClipboardStreamChunk).Select(c => c.Id), Has.None.EqualTo(dropped));
            Assert.That(slave.Sent.Of(MessageKind.ClipboardStreamAbort), Is.Empty, "a dropped stream is not aborted back");
        }
    }

    // the master did not take the answer streamed to it (its stream failed there), so its next pull asks a resend: answered
    // with the clipboard, never Unchanged as a clipboard already synced with it would be
    [Test]
    public async Task PullAsksAResend_AfterTheAnswerFailedAtTheMaster_IsAnsweredWithTheClipboard()
    {
        var image = BigImage(1);
        _clipboard.SetupImage(image);
        var slave = Slave();
        slave.Reliable.Hold();
        await Pull(slave);
        await slave.Sent.WaitFor(MessageKind.ClipboardStreamBegin, 1, Master);
        var failed = slave.Sent.Of<ClipboardStreamBeginMessage>(MessageKind.ClipboardStreamBegin).Single().Id;

        await slave.SimulateReceive(Master, MessageKind.ClipboardStreamAbort, new ClipboardStreamAbortMessage(failed, Failed: true).ToSaneJson());
        slave.Reliable.Release();
        await slave.SimulateReceive(Master, MessageKind.ClipboardPull, new ClipboardPullMessage(2, TextHash("on the master"), ResendFrom: 1).ToSaneJson());
        // an unchanged answer goes whole as the pull is answered, a full one streams from the sender's loop
        Assert.That(slave.Sent.Of(MessageKind.ClipboardPullResponse, Master), Is.Empty, "not answered unchanged");
        var answer = await SentAnswer(slave, 2);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(answer.PullId, Is.EqualTo(2));
            Assert.That(answer.Unchanged, Is.Not.True);
            Assert.That(answer.ImagePng, Is.EqualTo(image));
        }
    }

    // a pull asking no resend (the master took a newer copy, a plain abort) finds the clipboard it was answered with synced
    [Test]
    public async Task PullAsksNoResend_AfterTheAnswerWasSuperseded_IsAnsweredUnchanged()
    {
        _clipboard.SetupImage(BigImage(1));
        var slave = Slave();
        slave.Reliable.Hold();
        await Pull(slave);
        await slave.Sent.WaitFor(MessageKind.ClipboardStreamBegin, 1, Master);
        var superseded = slave.Sent.Of<ClipboardStreamBeginMessage>(MessageKind.ClipboardStreamBegin).Single().Id;

        await Abort(slave, superseded);
        slave.Reliable.Release();
        await Pull(slave, 2, masterHash: TextHash("on the master"));

        var answers = slave.Sent.Of<ClipboardPullResponseMessage>(MessageKind.ClipboardPullResponse, Master);
        using (Assert.EnterMultipleScope())
        {
            Assert.That(answers.Select(a => a.PullId), Is.EqualTo([2]));
            Assert.That(answers.Select(a => a.Unchanged), Is.EqualTo(new bool?[] { true }));
            Assert.That(slave.Sent.Of(MessageKind.ClipboardStreamBegin), Has.Count.EqualTo(1));
        }
    }

    [Test]
    public async Task SelectionOnlyNeed_ThenAStreamedPush_KeepsTheClipboardAndTakesTheSelection()
    {
        var image = BigImage(1);
        _clipboard.SetupImage(image);
        var slave = Slave();
        var selection = new string('s', 5000);
        var master = new ClipboardSnapshot(null, selection, image);

        // the master's clipboard is the image already here, its selection new: only that is asked for
        await slave.SimulateReceive(Master, MessageKind.ClipboardHash, ClipboardWire.ToHash(master).ToSaneJson());
        var push = ClipboardWire.ToPush(master);
        var stream = StreamedPush(2, push, ClipboardUtils.ClipboardHash(master));
        await Begin(slave, stream);
        var atBegin = new { _clipboard.ClearClipboardCallCount, _clipboard.ImagePng };
        await Chunks(slave, stream);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(slave.Sent.Of(MessageKind.ClipboardPullRequest), Is.Not.Empty, "the selection was asked for");
            Assert.That(atBegin.ClearClipboardCallCount, Is.Zero, "the clipboard already matches, never emptied");
            Assert.That(atBegin.ImagePng, Is.EqualTo(image));
            Assert.That(_clipboard.ClearClipboardCallCount, Is.Zero);
            Assert.That(_clipboard.SetClipboardCallCount, Is.Zero);
            Assert.That(_clipboard.ImagePng, Is.EqualTo(image));
            Assert.That(_clipboard.PrimaryText, Is.EqualTo(selection));
            Assert.That(slave.Sent.Of(MessageKind.ClipboardStreamAbort), Is.Empty);
            Assert.That(_osd.Shown.Count, Is.Zero);
        }
    }

    [Test]
    public async Task FailedClearAtBegin_IsWarnedAndRetriedAndNotTakenForAUserCopy()
    {
        _clipboard.SetupText("old on the slave");
        _clipboard.ClearFailsValue = true;
        _clipboard.CanUseEchoFallbackValue = false;
        var slave = Slave();
        var text = BigText('a');
        var stream = StreamedPush(6, new ClipboardPushMessage(text));

        await Begin(slave, stream);
        await Chunk(slave, stream.Chunks[0]);
        var retried = _clipboard.ClearClipboardCallCount;
        // the clipboard frees up: the next chunk's clear takes, and the clipboard is Hydra's again
        _clipboard.ClearFailsValue = false;
        _clipboard.CanUseEchoFallbackValue = true;
        await Chunk(slave, stream.Chunks[1]);
        var emptied = new { _clipboard.ClearClipboardCallCount, _clipboard.Text };
        await Chunks(slave, stream, from: 2);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(_log.Entries.Where(e => e.Level == LogLevel.Warning).Select(e => e.Message), Has.Some.Contains("could not be emptied"));
            Assert.That(retried, Is.EqualTo(2), "tried again with the chunk");
            Assert.That(emptied.ClearClipboardCallCount, Is.EqualTo(3));
            Assert.That(emptied.Text, Is.Null);
            Assert.That(slave.Sent.Of(MessageKind.ClipboardStreamAbort), Is.Empty, "a clipboard not yet emptied is no user copy");
            Assert.That(_clipboard.Text, Is.EqualTo(text));
            Assert.That(_osd.Shown.Count, Is.Zero);
        }
    }

    // -- helpers --

    private static async Task Depart(TestableSlaveRelay slave, bool disconnect)
    {
        if (disconnect) await slave.SimulateDisconnected();
        else await slave.Peers([]);
    }
}

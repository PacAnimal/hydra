using Cathedral.Extensions;
using Hydra.Config;
using Hydra.Platform;
using Hydra.Relay;
using Hydra.Screen;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Time.Testing;
using Tests.Setup;
using static Tests.Setup.ClipboardStreaming;

namespace Tests.Relay;

// the master's side of clipboard streaming: what InputRouter sends through the stream sender and takes from the receiver
[TestFixture]
public class ClipboardStreamRoutingTests : MasterClipboardFixture
{
    // -- the master's pushes --

    [Test]
    public async Task SmallClipboard_PullRequest_SendsOneWholePush()
    {
        var clipboard = new FakeClipboardSync();
        clipboard.SetupText("small");
        var (platform, relay, _) = await StartMaster(clipboard);
        TransitionTestHelper.EnterRemote(platform);

        await SimulatePullRequest(relay);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(Pushes(relay).Select(p => p.Text), Is.EqualTo(["small"]));
            Assert.That(relay.Sent.Of(MessageKind.ClipboardStreamBegin), Is.Empty);
        }
    }

    // a flick: the cursor left before the slave's request for its push arrived, which that visit still gets
    [Test]
    public async Task PullRequestFromTheLatestVisit_AfterTheCursorLeft_StillGetsItsPush()
    {
        var clipboard = new FakeClipboardSync();
        clipboard.SetupText("on the master");
        var (platform, relay, _) = await StartMaster(clipboard);
        VisitAndLeaveRemote(platform);
        var visit = relay.Sent.Of<ClipboardHashMessage>(MessageKind.ClipboardHash, "remote").Single().Visit;

        await relay.FireMessageReceived("remote", MessageKind.ClipboardPullRequest, new ClipboardPullRequestMessage(visit).ToSaneJson());

        using (Assert.EnterMultipleScope())
        {
            Assert.That(platform.IsOnVirtualScreen, Is.False, "pre-condition: the cursor left");
            Assert.That(visit, Is.Not.Null);
            Assert.That(Pushes(relay).Select(p => p.Text), Is.EqualTo(["on the master"]));
            Assert.That(relay.Sent.Of(MessageKind.ClipboardPushNone), Is.Empty);
        }
    }

    [Test]
    public async Task PullRequestFromAnOlderVisit_AfterTheCursorLeft_IsAnsweredWithNoPush()
    {
        var clipboard = new FakeClipboardSync();
        clipboard.SetupText("on the master");
        var (platform, relay, _) = await StartMaster(clipboard);
        VisitAndLeaveRemote(platform);
        var first = relay.Sent.Of<ClipboardHashMessage>(MessageKind.ClipboardHash, "remote").Single().Visit;
        VisitAndLeaveRemote(platform);

        await relay.FireMessageReceived("remote", MessageKind.ClipboardPullRequest, new ClipboardPullRequestMessage(first).ToSaneJson());

        using (Assert.EnterMultipleScope())
        {
            Assert.That(Pushes(relay), Is.Empty);
            Assert.That(relay.Sent.Of<ClipboardPushNoneMessage>(MessageKind.ClipboardPushNone, "remote").Select(n => n.Visit), Is.EqualTo([first]));
        }
    }

    [Test]
    public async Task PullRequest_TheMasterClipboardEmpty_IsAnsweredWithNoPush()
    {
        var clipboard = new FakeClipboardSync();
        var (platform, relay, _) = await StartMaster(clipboard);
        TransitionTestHelper.EnterRemote(platform);

        await relay.FireMessageReceived("remote", MessageKind.ClipboardPullRequest, new ClipboardPullRequestMessage(7).ToSaneJson());

        using (Assert.EnterMultipleScope())
        {
            Assert.That(Pushes(relay), Is.Empty);
            Assert.That(relay.Sent.Of<ClipboardPushNoneMessage>(MessageKind.ClipboardPushNone, "remote").Select(n => n.Visit), Is.EqualTo(new int?[] { 7 }));
        }
    }

    [Test]
    public async Task PullRequest_TheMasterClipboardUnreadable_IsAnsweredWithNoPush()
    {
        var clipboard = new FakeClipboardSync();
        clipboard.SetupText("on the master");
        var (platform, relay, _) = await StartMaster(clipboard);
        TransitionTestHelper.EnterRemote(platform);
        clipboard.ReadFailsValue = true;

        await SimulatePullRequest(relay);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(Pushes(relay), Is.Empty);
            Assert.That(relay.Sent.Of(MessageKind.ClipboardPushNone, "remote"), Has.Count.EqualTo(1));
        }
    }

    [Test]
    public async Task BigClipboard_PullRequest_StreamsThePushToThatHost()
    {
        var clipboard = new FakeClipboardSync();
        var image = BigImage(1);
        clipboard.SetupImage(image);
        var (platform, relay, _) = await StartMaster(clipboard);
        TransitionTestHelper.EnterRemote(platform);

        await SimulatePullRequest(relay);
        var push = await SentPush(relay, "remote");

        using (Assert.EnterMultipleScope())
        {
            Assert.That(push.ImagePng, Is.EqualTo(image));
            Assert.That(relay.Sent.Of(MessageKind.ClipboardPush), Is.Empty);
            Assert.That(relay.Sent.TargetsOf(MessageKind.ClipboardStreamChunk), Is.All.EqualTo("remote"));
        }
    }

    [Test]
    public async Task CursorMovesToASecondHostMidStream_ItsStreamGoesFirstAndBothComplete()
    {
        var clipboard = new FakeClipboardSync();
        var image = BigImage(1);
        clipboard.SetupImage(image);
        var (platform, relay, _) = await Services.StartOnline(
            TransitionTestHelper.CreateService(profile: TransitionTestHelper.ChainConfig, clipboard: clipboard), "remote", "remote2");
        TransitionTestHelper.EnterRemote(platform);
        relay.Reliable.Hold();
        await SimulatePullRequest(relay);
        await relay.Sent.WaitFor(MessageKind.ClipboardStreamBegin, 1, "remote");

        TransitionTestHelper.MoveRightTo(platform, relay, "remote2");
        await SimulatePullRequest(relay, "remote2");
        relay.Reliable.Release();
        var toFirst = await SentPush(relay, "remote");
        var toSecond = await SentPush(relay, "remote2");

        var order = relay.Sent.Snapshot().Where(s => s.Kind is MessageKind.ClipboardStreamBegin or MessageKind.ClipboardStreamChunk)
            .Select(s => s.Targets.Single()).ToList();
        var chunks = relay.Sent.Of(MessageKind.ClipboardStreamChunk, "remote").Count;
        using (Assert.EnterMultipleScope())
        {
            Assert.That(toFirst.ImagePng, Is.EqualTo(image));
            Assert.That(toSecond.ImagePng, Is.EqualTo(image));
            // the first host's Begin, then the cursor's host start to end, then the first host's chunks
            List<string> expected = ["remote", .. Enumerable.Repeat("remote2", 1 + chunks), .. Enumerable.Repeat("remote", chunks)];
            Assert.That(order, Is.EqualTo(expected));
        }
    }

    [TestCase(false)]
    [TestCase(true)]
    public async Task SlaveLeavesMidStream_NoFurtherChunksGoToIt(bool disconnect)
    {
        var clipboard = new FakeClipboardSync();
        clipboard.SetupImage(BigImage(1));
        var (platform, relay, _) = await StartMaster(clipboard);
        TransitionTestHelper.EnterRemote(platform);
        relay.Reliable.Hold();
        await SimulatePullRequest(relay);
        await relay.Sent.WaitFor(MessageKind.ClipboardStreamBegin, 1, "remote");
        var dropped = relay.Sent.Of<ClipboardStreamBeginMessage>(MessageKind.ClipboardStreamBegin).Single().Id;

        await Depart(relay, disconnect);
        relay.Reliable.Release();

        // a fresh stream to the host once it is back runs start to end, so the dropped one's step has long passed
        await TransitionTestHelper.BringRemoteOnline(relay);
        TransitionTestHelper.EnterRemote(platform);
        await SimulatePullRequest(relay);
        await relay.Sent.WaitFor(MessageKind.ClipboardStreamBegin, 2, "remote");
        await SentPush(relay, "remote");
        Assert.That(relay.Sent.Of<ClipboardStreamChunkMessage>(MessageKind.ClipboardStreamChunk).Select(c => c.Id), Has.None.EqualTo(dropped));
    }

    // -- the slaves' streamed pull responses --

    [Test]
    public async Task StreamedPullResponse_EmptiesAtBeginAndAppliesAfterTheLastChunk()
    {
        var clipboard = new FakeClipboardSync();
        clipboard.SetupText("old on the master");
        var (platform, relay, _) = await StartMaster(clipboard);
        VisitAndLeaveRemote(platform);
        var image = BigImage(2);
        var stream = StreamedAnswer(1, new ClipboardPullResponseMessage(PullIdTo(relay), null, ImagePng: image));

        await Begin(relay, stream);
        var emptied = new { clipboard.ClearClipboardCallCount, clipboard.Text, clipboard.ImagePng };
        await Chunks(relay, stream);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(emptied.ClearClipboardCallCount, Is.EqualTo(1));
            Assert.That(emptied.Text, Is.Null);
            Assert.That(emptied.ImagePng, Is.Null);
            Assert.That(clipboard.ImagePng, Is.EqualTo(image));
        }
    }

    // a Begin accepted, parked in its refusal check's read before the receiver takes it, when the cursor's next leave
    // pulls again: the accepted Begin's pull still streams in, so its answer lands once the newer pull finds nothing newer
    [Test]
    public async Task StreamedPullResponse_APullWhileItsBeginIsBeingTaken_StillLands()
    {
        var clipboard = new FakeClipboardSync { ChangeCountSupported = false };
        clipboard.SetupText("old on the master");
        var bundle = await StartMaster(clipboard);
        var pullId = await PullThenReenter(bundle);
        var image = BigImage(2);
        var stream = StreamedAnswer(1, new ClipboardPullResponseMessage(pullId, null, ImagePng: image));
        clipboard.ReadGate.Hold();

        var begun = Task.Run(() => Begin(bundle.Relay, stream));
        await clipboard.ReadsParked.Next("the Begin's refusal check");
        // the fire callback flushes the actor alone, never waiting on the parked read
        bundle.Platform.AfterFireCallback = bundle.Service.Flush;
        LeaveRemote(bundle.Platform);
        var newer = PullIdTo(bundle.Relay);
        clipboard.ReadGate.Release();
        await begun.WaitAsync(Bound);
        await Chunks(bundle.Relay, stream);
        await AnswerUnchanged(bundle.Relay, "remote");
        await bundle.Service.Flush();

        using (Assert.EnterMultipleScope())
        {
            Assert.That(newer, Is.Not.EqualTo(pullId), "pre-condition: a newer pull went out mid-Begin");
            Assert.That(clipboard.ImagePng, Is.EqualTo(image));
        }
    }

    [Test]
    public async Task StreamedPullResponse_WhenOnRemoteScreen_OffersItToTheActiveHost()
    {
        var clipboard = new FakeClipboardSync();
        clipboard.SetupText("master text");
        var bundle = await StartMaster(clipboard);
        var pullId = await PullThenReenter(bundle);
        var stream = StreamedAnswer(1, new ClipboardPullResponseMessage(pullId, null, ImagePng: BigImage(2)));

        await Begin(bundle.Relay, stream);
        var offeredEarly = bundle.Relay.Sent.Of(MessageKind.ClipboardHash).Count;
        await Chunks(bundle.Relay, stream);
        await bundle.Relay.Sent.WaitFor(MessageKind.ClipboardHash, 1, "remote");
        await bundle.Service.HashReads();

        using (Assert.EnterMultipleScope())
        {
            Assert.That(offeredEarly, Is.Zero);
            Assert.That(bundle.Relay.Sent.Of<ClipboardHashMessage>(MessageKind.ClipboardHash, "remote").Select(h => h.Hash),
                Is.EqualTo([ClipboardUtils.ClipboardHash(new ClipboardSnapshot(null, null, clipboard.ImagePng))]));
        }
    }

    [Test]
    public async Task StreamedPullResponse_ClipboardChangesHereMidStream_AbortsItAndKeepsTheLocalCopy()
    {
        var clipboard = new FakeClipboardSync();
        var (platform, relay, _) = await StartMaster(clipboard);
        VisitAndLeaveRemote(platform);
        var stream = StreamedAnswer(5, new ClipboardPullResponseMessage(PullIdTo(relay), null, ImagePng: BigImage(2)));
        await Begin(relay, stream);
        await Chunk(relay, stream.Chunks[0]);
        var setBefore = clipboard.SetClipboardCallCount;

        clipboard.CanUseEchoFallbackValue = false;
        await Chunks(relay, stream, from: 1);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(AbortsTo(relay.Sent, "remote"), Is.EqualTo([5L]));
            Assert.That(clipboard.SetClipboardCallCount, Is.EqualTo(setBefore));
            Assert.That(clipboard.ImagePng, Is.Null);
        }
    }

    [TestCase(false)]
    [TestCase(true)]
    public async Task SlaveLeavesMidStream_ItsStreamIsDropped(bool disconnect)
    {
        var clipboard = new FakeClipboardSync();
        var (platform, relay, _) = await StartMaster(clipboard);
        VisitAndLeaveRemote(platform);
        var stream = StreamedAnswer(1, new ClipboardPullResponseMessage(PullIdTo(relay), null, ImagePng: BigImage(2)));
        await Begin(relay, stream);
        await Chunk(relay, stream.Chunks[0]);

        await Depart(relay, disconnect);
        await Chunks(relay, stream, from: 1);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(clipboard.SetClipboardCallCount, Is.Zero);
            Assert.That(clipboard.ImagePng, Is.Null);
        }
    }

    [Test]
    public async Task StreamedPullResponse_BadBlobHash_ShowsTheFailure()
    {
        var clipboard = new FakeClipboardSync();
        var osd = new RecordingOsd();
        var log = new CapturingLog<InputRouter>();
        var (platform, relay, _) = await Services.StartOnline(TransitionTestHelper.CreateService(clipboard: clipboard, osd: osd, log: log));
        VisitAndLeaveRemote(platform);
        var stream = StreamedAnswer(1, new ClipboardPullResponseMessage(PullIdTo(relay), null, ImagePng: BigImage(2)), blobHash: 12345);

        await Begin(relay, stream);
        await Chunks(relay, stream);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(await osd.Shown.Next("the failure"), Is.EqualTo(ClipboardStreamReceiver.FailedOsd));
            Assert.That(log.Entries.Where(e => e.Level == LogLevel.Warning).Select(e => e.Message), Has.Some.Contains("remote").And.Contains("match"));
            Assert.That(clipboard.SetClipboardCallCount, Is.Zero);
            Assert.That(clipboard.ImagePng, Is.Null);
        }
    }

    [Test]
    public async Task StreamedPullResponse_NoChunkWithinTheWatchdog_ShowsTheFailure()
    {
        var clipboard = new FakeClipboardSync();
        var osd = new RecordingOsd();
        var clock = new FakeTimeProvider();
        var log = new CapturingLog<InputRouter>();
        var (platform, relay, _) = await Services.StartOnline(
            TransitionTestHelper.CreateService(clipboard: clipboard, osd: osd, timeProvider: clock, log: log));
        VisitAndLeaveRemote(platform);
        var stream = StreamedAnswer(1, new ClipboardPullResponseMessage(PullIdTo(relay), null, ImagePng: BigImage(2)));
        await Begin(relay, stream);
        await Chunk(relay, stream.Chunks[0]);

        clock.Advance(ClipboardStreamReceiver.Watchdog);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(await osd.Shown.Next("the failure"), Is.EqualTo(ClipboardStreamReceiver.FailedOsd));
            Assert.That(log.Entries.Where(e => e.Level == LogLevel.Warning).Select(e => e.Message), Has.Some.Contains("remote").And.Contains("timed out"));
            Assert.That(clipboard.ImagePng, Is.Null);
        }
    }

    [Test]
    public async Task StreamedPullResponse_BadBlobHash_AbortsTheSlave()
    {
        var (platform, relay, _) = await StartMaster(new FakeClipboardSync());
        VisitAndLeaveRemote(platform);
        var stream = StreamedAnswer(3, new ClipboardPullResponseMessage(PullIdTo(relay), null, ImagePng: BigImage(2)), blobHash: 12345);

        await Begin(relay, stream);
        await Chunks(relay, stream);

        Assert.That(AbortsTo(relay.Sent, "remote"), Is.EqualTo([3L]));
    }

    [Test]
    public async Task StreamedPullResponse_NoChunkWithinTheWatchdog_AbortsTheSlave()
    {
        var clock = new FakeTimeProvider();
        var (platform, relay, _) = await Services.StartOnline(TransitionTestHelper.CreateService(clipboard: new FakeClipboardSync(), timeProvider: clock));
        VisitAndLeaveRemote(platform);
        var stream = StreamedAnswer(3, new ClipboardPullResponseMessage(PullIdTo(relay), null, ImagePng: BigImage(2)));
        await Begin(relay, stream);
        await Chunk(relay, stream.Chunks[0]);

        clock.Advance(ClipboardStreamReceiver.Watchdog);

        await relay.Sent.WaitFor(MessageKind.ClipboardStreamAbort, 1, "remote");
        Assert.That(AbortsTo(relay.Sent, "remote"), Is.EqualTo([3L]));
    }

    // a user's copy is newer than anything a broken Begin says is coming, so it stays; still a failure
    [TestCase(false)]
    [TestCase(true)]
    public async Task StreamBegin_Rejected_KeepsTheUsersCopy_ShowsTheFailure_AndAbortsTheSlave(bool aPush)
    {
        var clipboard = new FakeClipboardSync();
        clipboard.SetupText("old on the master");
        var osd = new RecordingOsd();
        var log = new CapturingLog<InputRouter>();
        var (platform, relay, _) = await Services.StartOnline(TransitionTestHelper.CreateService(clipboard: clipboard, osd: osd, log: log));
        VisitAndLeaveRemote(platform);
        var begin = aPush
            ? new ClipboardStreamBeginMessage(6, MessageKind.ClipboardPush, 1024 * 1024, 0, ContentHash)
            : new ClipboardStreamBeginMessage(6, MessageKind.ClipboardPullResponse, 0, 0, ContentHash, PullIdTo(relay));

        await relay.FireMessageReceived("remote", MessageKind.ClipboardStreamBegin, begin.ToSaneJson());

        using (Assert.EnterMultipleScope())
        {
            Assert.That(await osd.Shown.Next("the failure"), Is.EqualTo(ClipboardStreamReceiver.FailedOsd));
            Assert.That(log.Entries.Where(e => e.Level == LogLevel.Warning).Select(e => e.Message), Has.Some.Contains("remote").And.Contains("Rejected"));
            Assert.That(clipboard.ClearClipboardCallCount, Is.Zero);
            Assert.That(clipboard.Text, Is.EqualTo("old on the master"));
            Assert.That(AbortsTo(relay.Sent, "remote"), Is.EqualTo([6L]));
        }
    }

    // still told a newer clipboard is coming, so Hydra's old write never stays pasteable
    [TestCase(false)]
    [TestCase(true)]
    public async Task StreamBegin_Rejected_EmptiesHydrasWrite_AndShowsTheFailure(bool aPush)
    {
        var clipboard = new FakeClipboardSync();
        var osd = new RecordingOsd();
        var (platform, relay, _) = await Services.StartOnline(TransitionTestHelper.CreateService(clipboard: clipboard, osd: osd));
        VisitAndLeaveRemote(platform);
        await AnswerPull(relay, new ClipboardPullResponseMessage(PullIdTo(relay), "from the remote"));
        Assert.That(clipboard.Text, Is.EqualTo("from the remote"), "pre-condition: hydra's write");
        VisitAndLeaveRemote(platform);
        var begin = aPush
            ? new ClipboardStreamBeginMessage(6, MessageKind.ClipboardPush, 1024 * 1024, 0, ContentHash)
            : new ClipboardStreamBeginMessage(6, MessageKind.ClipboardPullResponse, 0, 0, ContentHash, PullIdTo(relay));

        await relay.FireMessageReceived("remote", MessageKind.ClipboardStreamBegin, begin.ToSaneJson());

        using (Assert.EnterMultipleScope())
        {
            Assert.That(await osd.Shown.Next("the failure"), Is.EqualTo(ClipboardStreamReceiver.FailedOsd));
            Assert.That(clipboard.ClearClipboardCallCount, Is.EqualTo(1));
            Assert.That(clipboard.Text, Is.Null);
        }
    }

    [Test]
    public async Task StreamedPullResponse_CompleteButUnparseable_ShowsTheFailure_AndAbortsTheSlave()
    {
        var clipboard = new FakeClipboardSync();
        var osd = new RecordingOsd();
        var (platform, relay, _) = await Services.StartOnline(TransitionTestHelper.CreateService(clipboard: clipboard, osd: osd));
        VisitAndLeaveRemote(platform);
        byte[] blob = [(byte)MessageKind.ClipboardPullResponse, .. "not json"u8.ToArray(), .. new byte[ClipboardStreamSender.Threshold]];
        var stream = StreamedClipboard.Split(3, blob, ClipboardStreamSender.ChunkBytes, MessageKind.ClipboardPullResponse, ContentHash,
            pullId: PullIdTo(relay));

        await Begin(relay, stream);
        await Chunks(relay, stream);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(await osd.Shown.Next("the failure"), Is.EqualTo(ClipboardStreamReceiver.FailedOsd));
            Assert.That(clipboard.SetClipboardCallCount, Is.Zero);
            Assert.That(AbortsTo(relay.Sent, "remote"), Is.EqualTo([3L]));
        }
    }

    [Test]
    public async Task StreamedPullResponse_TheSlaveGaveUp_ShowsTheFailure_AndAbortsNothingBack()
    {
        var clipboard = new FakeClipboardSync();
        var osd = new RecordingOsd();
        var log = new CapturingLog<InputRouter>();
        var (platform, relay, service) = await Services.StartOnline(TransitionTestHelper.CreateService(clipboard: clipboard, osd: osd, log: log));
        VisitAndLeaveRemote(platform);
        var stream = StreamedAnswer(4, new ClipboardPullResponseMessage(PullIdTo(relay), null, ImagePng: BigImage(2)));
        await Begin(relay, stream);
        await Chunk(relay, stream.Chunks[0]);

        await relay.FireMessageReceived("remote", MessageKind.ClipboardStreamAbort, new ClipboardStreamAbortMessage(4, Failed: true).ToSaneJson());
        await Chunks(relay, stream, from: 1);
        await service.Flush();

        using (Assert.EnterMultipleScope())
        {
            Assert.That(await osd.Shown.Next("the failure"), Is.EqualTo(ClipboardStreamReceiver.FailedOsd));
            Assert.That(log.Entries.Where(e => e.Level == LogLevel.Warning).Select(e => e.Message), Has.Some.Contains("remote").And.Contains("gave up"));
            Assert.That(clipboard.SetClipboardCallCount, Is.Zero);
            Assert.That(relay.Sent.Of(MessageKind.ClipboardStreamAbort, "remote"), Is.Empty, "the slave already dropped it");
        }
    }

    // an older stream's watchdog fires on the timer thread, held between dropping it and raising its failure while a
    // newer stream from the same host begins: the late failure must leave the newer one's pull streaming
    [Test]
    public async Task AnOlderStreamFailsLate_AfterANewerOneFromThatHostBegan_TheNewerOneStillLands()
    {
        var clipboard = new FakeClipboardSync();
        var clock = new FakeTimeProvider();
        var log = new HeldOnTimeoutLog();
        var (platform, relay, service) = await Services.StartOnline(TransitionTestHelper.CreateService(clipboard: clipboard, timeProvider: clock, log: log));
        VisitAndLeaveRemote(platform);
        var older = StreamedAnswer(1, new ClipboardPullResponseMessage(PullIdTo(relay), null, ImagePng: BigImage(1)));
        await Begin(relay, older);
        await Chunk(relay, older.Chunks[0]);
        VisitAndLeaveRemote(platform);
        var image = BigImage(2);
        var newer = StreamedAnswer(2, new ClipboardPullResponseMessage(PullIdTo(relay), null, ImagePng: image));

        var watchdog = Task.Run(() => clock.Advance(ClipboardStreamReceiver.Watchdog));
        await log.Reached.Task.WaitAsync(Bound);
        await Begin(relay, newer);
        log.Gate.Release();
        await watchdog.WaitAsync(Bound);
        await Chunks(relay, newer);
        await service.Flush();

        Assert.That(clipboard.ImagePng is { } applied && applied.AsSpan().SequenceEqual(image), Is.True, "the newer stream landed");
    }

    [Test]
    public async Task StreamedPullResponse_AbortedByTheSlave_ShowsNothingAndAppliesNothing()
    {
        var clipboard = new FakeClipboardSync();
        var osd = new RecordingOsd();
        var clock = new FakeTimeProvider();
        var (platform, relay, service) = await Services.StartOnline(TransitionTestHelper.CreateService(clipboard: clipboard, osd: osd, timeProvider: clock));
        VisitAndLeaveRemote(platform);
        var stream = StreamedAnswer(4, new ClipboardPullResponseMessage(PullIdTo(relay), null, ImagePng: BigImage(2)));
        await Begin(relay, stream);
        await Chunk(relay, stream.Chunks[0]);

        await Abort(relay, 4);
        await Chunks(relay, stream, from: 1);
        // a stream still pending would time out here, its failure shown from the router's actor
        clock.Advance(ClipboardStreamReceiver.Watchdog);
        await service.Flush();

        using (Assert.EnterMultipleScope())
        {
            Assert.That(osd.Shown.Count, Is.Zero);
            Assert.That(clipboard.SetClipboardCallCount, Is.Zero);
            Assert.That(clipboard.ImagePng, Is.Null);
        }
    }

    [Test]
    public async Task WholePullResponseWhileAStreamIsPending_IsAppliedAndTheStreamDropped()
    {
        var clipboard = new FakeClipboardSync();
        var osd = new RecordingOsd();
        var clock = new FakeTimeProvider();
        var (platform, relay, service) = await Services.StartOnline(TransitionTestHelper.CreateService(clipboard: clipboard, osd: osd, timeProvider: clock));
        VisitAndLeaveRemote(platform);
        var pullId = PullIdTo(relay);
        var stream = StreamedAnswer(1, new ClipboardPullResponseMessage(pullId, null, ImagePng: BigImage(2)));
        await Begin(relay, stream);
        await Chunk(relay, stream.Chunks[0]);

        await AnswerPull(relay, new ClipboardPullResponseMessage(pullId, "whole"));
        var setAfterWhole = clipboard.SetClipboardCallCount;
        await Chunk(relay, stream.Chunks[1]);
        // a stream still pending would time out here, its failure shown from the router's actor
        clock.Advance(ClipboardStreamReceiver.Watchdog);
        await service.Flush();

        using (Assert.EnterMultipleScope())
        {
            Assert.That(clipboard.Text, Is.EqualTo("whole"));
            Assert.That(clipboard.SetClipboardCallCount, Is.EqualTo(setAfterWhole));
            Assert.That(osd.Shown.Count, Is.Zero);
        }
    }

    // -- slave to slave crossings: the entry hash waits for the pull's answer --

    [Test]
    public async Task Crossing_AnswerChangesTheClipboard_TheNextHostGetsOnlyItsHashAfterTheAnswer()
    {
        var clipboard = new FakeClipboardSync();
        clipboard.SetupText("old on the master");
        var (platform, relay, service) = await StartChain(clipboard);
        TransitionTestHelper.EnterRemote(platform);
        TransitionTestHelper.MoveRightTo(platform, relay, "remote2");
        var early = Hashes(relay, "remote2").Count;

        await AnswerPull(relay, new ClipboardPullResponseMessage(PullIdTo(relay, "remote"), "from remote"));
        await service.HashReads();

        using (Assert.EnterMultipleScope())
        {
            Assert.That(early, Is.Zero);
            Assert.That(Hashes(relay, "remote2").Select(h => h.Hash), Is.EqualTo([TextHash("from remote")]));
        }
    }

    [Test]
    public async Task Crossing_AnswerUnchanged_TheNextHostGetsTheMastersHashOnceAfterTheAnswer()
    {
        var clipboard = new FakeClipboardSync();
        clipboard.SetupText("on the master");
        var (platform, relay, service) = await StartChain(clipboard);
        TransitionTestHelper.EnterRemote(platform);
        TransitionTestHelper.MoveRightTo(platform, relay, "remote2");
        var early = Hashes(relay, "remote2").Count;

        await AnswerPull(relay, new ClipboardPullResponseMessage(PullIdTo(relay, "remote"), null, Unchanged: true));
        await relay.Sent.WaitFor(MessageKind.ClipboardHash, 1, "remote2");
        await service.HashReads();

        using (Assert.EnterMultipleScope())
        {
            Assert.That(early, Is.Zero);
            Assert.That(Hashes(relay, "remote2").Select(h => h.Hash), Is.EqualTo([TextHash("on the master")]));
        }
    }

    [Test]
    public async Task Crossing_AnswerStreams_TheNextHostGetsPendingAtBeginThenTheRealHash()
    {
        var clipboard = new FakeClipboardSync();
        clipboard.SetupText("old on the master");
        var (platform, relay, service) = await StartChain(clipboard);
        TransitionTestHelper.EnterRemote(platform);
        TransitionTestHelper.MoveRightTo(platform, relay, "remote2");
        var image = BigImage(2);
        var stream = StreamedAnswer(1, new ClipboardPullResponseMessage(PullIdTo(relay, "remote"), null, ImagePng: image));
        var early = Hashes(relay, "remote2").Count;

        await Begin(relay, stream);
        var atBegin = Hashes(relay, "remote2");
        await Chunks(relay, stream, to: stream.Chunks.Length - 1);
        var beforeLast = Hashes(relay, "remote2").Count;
        await Chunk(relay, stream.Chunks[^1]);
        await relay.Sent.WaitFor(MessageKind.ClipboardHash, 2, "remote2");
        await service.HashReads();

        var hashes = Hashes(relay, "remote2");
        using (Assert.EnterMultipleScope())
        {
            Assert.That(early, Is.Zero);
            Assert.That(atBegin, Is.EqualTo([new ClipboardHashMessage(null, Pending: true)]));
            Assert.That(beforeLast, Is.EqualTo(1));
            Assert.That(hashes, Has.Count.EqualTo(2));
            Assert.That(hashes[^1].Hash, Is.EqualTo(ClipboardUtils.ClipboardHash(new ClipboardSnapshot(null, null, image))));
            Assert.That(hashes[^1].Pending, Is.Null);
        }
    }

    [Test]
    public async Task Crossing_UserCopiesOnTheMasterMidStream_TheNextHostGetsThatCopysHash()
    {
        var clipboard = new FakeClipboardSync();
        clipboard.SetupText("old on the master");
        var (platform, relay, service) = await StartChain(clipboard);
        TransitionTestHelper.EnterRemote(platform);
        TransitionTestHelper.MoveRightTo(platform, relay, "remote2");
        var stream = StreamedAnswer(1, new ClipboardPullResponseMessage(PullIdTo(relay, "remote"), null, ImagePng: BigImage(2)));
        await Begin(relay, stream);
        await Chunk(relay, stream.Chunks[0]);

        clipboard.CanUseEchoFallbackValue = false;
        clipboard.SetupText("copied here");
        await Chunk(relay, stream.Chunks[1]);
        await service.Flush();
        await relay.Sent.WaitFor(MessageKind.ClipboardHash, 2, "remote2");
        await service.HashReads();

        var hashes = Hashes(relay, "remote2");
        using (Assert.EnterMultipleScope())
        {
            Assert.That(hashes.Select(h => h.Pending), Is.EqualTo(new bool?[] { true, null }));
            Assert.That(hashes.Select(h => h.Hash), Is.EqualTo(new ulong?[] { null, TextHash("copied here") }));
        }
    }

    [Test]
    public async Task Crossing_NoAnswer_TheNextHostGetsTheCurrentHashOnceTheWaitRunsOut()
    {
        var clipboard = new FakeClipboardSync();
        clipboard.SetupText("on the master");
        var clock = new FakeTimeProvider();
        var (platform, relay, service) = await StartChain(clipboard, clock);
        TransitionTestHelper.EnterRemote(platform);
        TransitionTestHelper.MoveRightTo(platform, relay, "remote2");

        clock.Advance(DeferredClipboardOffer.Wait - TimeSpan.FromMilliseconds(1));
        await service.Flush();
        await service.HashReads();
        var early = Hashes(relay, "remote2").Count;
        clock.Advance(TimeSpan.FromMilliseconds(1));
        await service.Flush();
        await relay.Sent.WaitFor(MessageKind.ClipboardHash, 1, "remote2");
        var expired = Hashes(relay, "remote2").Select(h => h.Hash).ToList();
        // a late answer finds nothing waiting, and applies nothing new, so it offers nothing more
        await AnswerPull(relay, new ClipboardPullResponseMessage(PullIdTo(relay, "remote"), null, Unchanged: true));
        await service.HashReads();

        using (Assert.EnterMultipleScope())
        {
            Assert.That(early, Is.Zero);
            Assert.That(expired, Is.EqualTo([TextHash("on the master")]));
            Assert.That(Hashes(relay, "remote2"), Has.Count.EqualTo(1));
        }
    }

    [Test]
    public async Task Crossing_OnToAThirdHostBeforeTheAnswer_OnlyThatHostGetsTheHash()
    {
        var clipboard = new FakeClipboardSync();
        clipboard.SetupText("on the master");
        var (platform, relay, service) = await Services.StartOnline(
            TransitionTestHelper.CreateService(profile: Chain3Config, clipboard: clipboard), "remote", "remote2", "remote3");
        TransitionTestHelper.EnterRemote(platform);
        TransitionTestHelper.MoveRightTo(platform, relay, "remote2");
        var fromFirst = PullIdTo(relay, "remote");
        TransitionTestHelper.MoveRightTo(platform, relay, "remote3");

        await AnswerPull(relay, new ClipboardPullResponseMessage(fromFirst, "from remote"));
        await AnswerPull(relay, new ClipboardPullResponseMessage(PullIdTo(relay, "remote2"), "from remote2"), "remote2");
        // the answer's hash is read off the actor
        await service.HashReads();

        using (Assert.EnterMultipleScope())
        {
            Assert.That(Hashes(relay, "remote2"), Is.Empty);
            Assert.That(Hashes(relay, "remote3").Select(h => h.Hash), Is.EqualTo([TextHash("from remote2")]));
        }
    }

    [Test]
    public async Task EntryFromTheLocalScreenWithNoPullOutstanding_SendsTheHashAtOnce()
    {
        var clipboard = new FakeClipboardSync();
        clipboard.SetupText("on the master");
        var (platform, relay, _) = await StartChain(clipboard);

        TransitionTestHelper.EnterRemote(platform);
        await relay.Sent.WaitFor(MessageKind.ClipboardHash, 1, "remote");

        Assert.That(Hashes(relay, "remote").Select(h => h.Hash), Is.EqualTo([TextHash("on the master")]));
    }

    [TestCase(false)]
    [TestCase(true)]
    public async Task PullHostLeaves_ItsLaterAnswerIsIgnored(bool disconnect)
    {
        var clipboard = new FakeClipboardSync();
        clipboard.SetupText("on the master");
        var (platform, relay, _) = await StartMaster(clipboard);
        VisitAndLeaveRemote(platform);
        var pullId = PullIdTo(relay);

        await Depart(relay, disconnect);
        await TransitionTestHelper.BringRemoteOnline(relay);
        await AnswerPull(relay, new ClipboardPullResponseMessage(pullId, "from before it left"));

        using (Assert.EnterMultipleScope())
        {
            Assert.That(clipboard.Text, Is.EqualTo("on the master"));
            Assert.That(clipboard.SetClipboardCallCount, Is.Zero);
        }
    }

    [Test]
    public async Task Crossing_PullHostLeavesBeforeAnswering_TheNextHostGetsTheCurrentHash()
    {
        var clipboard = new FakeClipboardSync();
        clipboard.SetupText("on the master");
        var (platform, relay, service) = await StartChain(clipboard);
        TransitionTestHelper.EnterRemote(platform);
        TransitionTestHelper.MoveRightTo(platform, relay, "remote2");

        await relay.FirePeersChanged("remote2");
        await relay.Sent.WaitFor(MessageKind.ClipboardHash, 1, "remote2");
        await service.HashReads();

        Assert.That(Hashes(relay, "remote2").Select(h => h.Hash), Is.EqualTo([TextHash("on the master")]));
    }

    // -- from a slave back onto the master's screen, which pulls, then on to another slave while the answer streams --

    [Test]
    public async Task ViaTheMaster_OnToAnotherHostMidStream_ItGetsPendingThenTheRealHash()
    {
        var clipboard = new FakeClipboardSync();
        clipboard.SetupText("old on the master");
        var (platform, relay, _) = await StartSides(clipboard);
        VisitAndLeaveRemote(platform);
        var image = BigImage(2);
        var stream = StreamedAnswer(1, new ClipboardPullResponseMessage(PullIdTo(relay, "remote"), null, ImagePng: image));
        await Begin(relay, stream);
        await Chunk(relay, stream.Chunks[0]);

        EnterLeftHost(platform, relay);
        var atEntry = Hashes(relay, "remote2");
        await Chunks(relay, stream, from: 1, to: stream.Chunks.Length - 1);
        var beforeLast = Hashes(relay, "remote2").Count;
        await Chunk(relay, stream.Chunks[^1]);
        await relay.Sent.WaitFor(MessageKind.ClipboardHash, 2, "remote2");
        var hashes = Hashes(relay, "remote2");
        await SimulatePullRequest(relay, "remote2");
        var push = await SentPush(relay, "remote2");

        using (Assert.EnterMultipleScope())
        {
            Assert.That(atEntry, Is.EqualTo([new ClipboardHashMessage(null, Pending: true)]));
            Assert.That(beforeLast, Is.EqualTo(1));
            Assert.That(clipboard.ImagePng, Is.EqualTo(image));
            Assert.That(hashes, Has.Count.EqualTo(2));
            Assert.That(hashes[^1].Hash, Is.EqualTo(ClipboardUtils.ClipboardHash(new ClipboardSnapshot(null, null, image))));
            Assert.That(hashes[^1].Pending, Is.Null);
            Assert.That(push.ImagePng, Is.EqualTo(image));
            Assert.That(relay.Sent.Of(MessageKind.ClipboardPush, "remote2"), Is.Empty);
        }
    }

    [TestCase(false)]
    [TestCase(true)]
    public async Task ViaTheMaster_StreamFailsAfterEnteringAnotherHost_ItGetsNoStaleHashOfTheMastersOldClipboard(bool watchdog)
    {
        var clipboard = new FakeClipboardSync();
        clipboard.SetupText("old on the master");
        var clock = new FakeTimeProvider();
        var (platform, relay, service) = await StartSides(clipboard, clock);
        VisitAndLeaveRemote(platform);
        var stream = StreamedAnswer(1, new ClipboardPullResponseMessage(PullIdTo(relay, "remote"), null, ImagePng: BigImage(2)),
            blobHash: watchdog ? null : 12345);
        await Begin(relay, stream);
        await Chunk(relay, stream.Chunks[0]);
        EnterLeftHost(platform, relay);

        if (watchdog) clock.Advance(ClipboardStreamReceiver.Watchdog);
        else await Chunks(relay, stream, from: 1);
        await service.Flush();
        await service.HashReads();

        using (Assert.EnterMultipleScope())
        {
            // the master's clipboard is left empty, so the host told to empty and wait is offered nothing more
            Assert.That(clipboard.Text, Is.Null);
            Assert.That(clipboard.ImagePng, Is.Null);
            Assert.That(Hashes(relay, "remote2"), Is.EqualTo([new ClipboardHashMessage(null, Pending: true)]));
            Assert.That(Hashes(relay, "remote2").Select(h => h.Hash), Has.None.EqualTo(TextHash("old on the master")));
        }
    }

    // told Pending, it is sent its hash once the stream lands though the cursor left, stamped with its latest visit
    [Test]
    public async Task ViaTheMaster_OtherHostLeftBeforeTheStreamCompletes_ItIsStillSentItsHash()
    {
        var clipboard = new FakeClipboardSync();
        clipboard.SetupText("old on the master");
        var (platform, relay, service) = await StartSides(clipboard);
        VisitAndLeaveRemote(platform);
        var image = BigImage(2);
        var stream = StreamedAnswer(1, new ClipboardPullResponseMessage(PullIdTo(relay, "remote"), null, ImagePng: image));
        await Begin(relay, stream);
        await Chunk(relay, stream.Chunks[0]);
        EnterLeftHost(platform, relay);
        LeaveLeftHost(platform);
        var whenLeft = Hashes(relay, "remote2");

        await Chunks(relay, stream, from: 1);
        await service.Flush();
        await service.HashReads();

        var hashes = Hashes(relay, "remote2");
        using (Assert.EnterMultipleScope())
        {
            Assert.That(whenLeft, Is.EqualTo([new ClipboardHashMessage(null, Pending: true)]));
            Assert.That(hashes, Has.Count.EqualTo(2));
            Assert.That(hashes[^1].Hash, Is.EqualTo(ClipboardUtils.ClipboardHash(new ClipboardSnapshot(null, null, image))));
            Assert.That(hashes[^1].Visit, Is.Not.Null, "stamped with its visit");
            Assert.That(clipboard.ImagePng, Is.EqualTo(image));
        }
    }

    // remote streams its answer to an older pull while a newer pull to it is out: only the streaming pull waits untimed,
    // so the newer one, never answered, still runs out. The stream completing after that is held for the newer pull, which
    // was waited out already, so it lands at once, and the host told Pending is sent its clipboard
    [Test]
    public async Task ViaTheMaster_ANewerPullBesideTheStreamIsNeverAnswered_TheWaitingHostIsStillToldOnceItRunsOut()
    {
        var clipboard = new FakeClipboardSync();
        clipboard.SetupText("old on the master");
        var clock = new FakeTimeProvider();
        var (platform, relay, service) = await StartSides(clipboard, clock);
        VisitAndLeaveRemote(platform);
        var image = BigImage(2);
        var stream = StreamedAnswer(1, new ClipboardPullResponseMessage(PullIdTo(relay, "remote"), null, ImagePng: image));
        await Begin(relay, stream);
        await Chunk(relay, stream.Chunks[0]);
        VisitAndLeaveRemote(platform);
        Assert.That(relay.Sent.Of(MessageKind.ClipboardPull, "remote"), Has.Count.EqualTo(2), "pre-condition: a newer pull beside the stream");
        EnterLeftHost(platform, relay);
        var whenEntered = Hashes(relay, "remote2");

        clock.Advance(DeferredClipboardOffer.Wait);
        await service.Flush();
        await Chunks(relay, stream, from: 1);
        await service.Flush();
        await service.HashReads();

        using (Assert.EnterMultipleScope())
        {
            Assert.That(whenEntered, Is.EqualTo([new ClipboardHashMessage(null, Pending: true)]), "pre-condition: waits on the stream");
            Assert.That(clipboard.ImagePng, Is.EqualTo(image), "the held stream landed, its newer pull waited out");
            Assert.That(relay.Sent.Of(MessageKind.ClipboardPushNone, "remote2"), Is.Empty);
            Assert.That(Hashes(relay, "remote2").Select(h => h.Hash), Is.EqualTo(new ulong?[] { null, ImageHash(image) }), "its wait ended");
        }
    }

    // a newer copy whose write failed still supersedes the older pull: the older stream still arriving is dropped and
    // aborted, never written over the master's clipboard, and the newer copy's host is asked again on its next pull
    [Test]
    public async Task ViaTheMaster_ANewerCopyCannotBeWritten_TheOlderStreamNeverLandsAndTheNewerIsAskedAgain()
    {
        var clipboard = new FakeClipboardSync();
        clipboard.SetupText("old on the master");
        var (platform, relay, service) = await StartSides(clipboard, new FakeTimeProvider());
        VisitAndLeaveRemote(platform);
        var stream = StreamedAnswer(1, new ClipboardPullResponseMessage(PullIdTo(relay, "remote"), null, ImagePng: BigImage(2)));
        await Begin(relay, stream);
        await Chunk(relay, stream.Chunks[0]);
        EnterLeftHost(platform, relay);
        LeaveLeftHost(platform);
        var newer = PullIdTo(relay, "remote2");

        clipboard.WriteFailsValue = true;
        await AnswerPull(relay, new ClipboardPullResponseMessage(newer, "newer on remote2"), "remote2");
        clipboard.WriteFailsValue = false;
        await Chunks(relay, stream, from: 1);
        await service.Flush();
        EnterLeftHost(platform, relay, 2);
        LeaveLeftHost(platform);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(clipboard.ImagePng, Is.Null, "the older stream never landed");
            Assert.That(AbortsTo(relay.Sent, "remote"), Is.EqualTo([stream.Begin.Id]));
            Assert.That(LatestPullTo(relay, "remote2").ResendFrom, Is.EqualTo(newer));
        }
    }

    // remote2, told Pending, is owed its hash once remote's stream lands; the cursor goes back over remote, whose newer
    // pull is out when the stream completes, so the stream is held for that pull: remote2 waits on it, never told none
    // follows, and is sent the clipboard once that pull's unchanged answer lands the stream
    [Test]
    public async Task ViaTheMaster_TheStreamItWaitsOnIsHeldForANewerPull_ItIsSentItsHashOnceThatLands()
    {
        var clipboard = new FakeClipboardSync();
        clipboard.SetupText("old on the master");
        var (platform, relay, service) = await StartSides(clipboard, new FakeTimeProvider());
        VisitAndLeaveRemote(platform);
        var image = BigImage(2);
        var stream = StreamedAnswer(1, new ClipboardPullResponseMessage(PullIdTo(relay, "remote"), null, ImagePng: image));
        await Begin(relay, stream);
        await Chunk(relay, stream.Chunks[0]);
        EnterLeftHost(platform, relay);
        LeaveLeftHost(platform);
        VisitAndLeaveRemote(platform);
        var newer = PullIdTo(relay, "remote");

        await Chunks(relay, stream, from: 1);
        await service.Flush();
        await service.HashReads();
        var whileHeld = new { Hashes = Hashes(relay, "remote2"), None = relay.Sent.Of(MessageKind.ClipboardPushNone, "remote2").Count, clipboard.ImagePng };
        await AnswerPull(relay, new ClipboardPullResponseMessage(newer, null, Unchanged: true));
        await service.Flush();
        await service.HashReads();

        using (Assert.EnterMultipleScope())
        {
            Assert.That(newer, Is.Not.EqualTo(stream.Begin.PullId), "pre-condition: a newer pull to remote");
            Assert.That(whileHeld.ImagePng, Is.Null, "pre-condition: the stream is held");
            Assert.That(whileHeld.Hashes, Is.EqualTo([new ClipboardHashMessage(null, Pending: true)]), "pre-condition: told Pending only");
            Assert.That(whileHeld.None, Is.Zero, "never told none follows while the stream is held");
            Assert.That(clipboard.ImagePng, Is.EqualTo(image));
            Assert.That(Hashes(relay, "remote2").Select(h => h.Hash), Is.EqualTo(new ulong?[] { null, ImageHash(image) }));
            Assert.That(relay.Sent.Of(MessageKind.ClipboardPushNone, "remote2"), Is.Empty);
        }
    }

    // as above, remote2's owed read already parked when the stream completes and is held: what waited on the stream waits
    // on the newer pull, so remote2 is not released by the hold, and is sent the clipboard once that pull's answer lands it
    [Test]
    public async Task ViaTheMaster_TheStreamItWaitsOnIsHeldWhileAReadIsUnderWay_ItIsSentItsHashOnceThatLands()
    {
        var clipboard = new FakeClipboardSync();
        clipboard.SetupText("old on the master");
        var (platform, relay, service) = await StartSides(clipboard, new FakeTimeProvider());
        VisitAndLeaveRemote(platform);
        var image = BigImage(2);
        var stream = StreamedAnswer(1, new ClipboardPullResponseMessage(PullIdTo(relay, "remote"), null, ImagePng: image));
        await Begin(relay, stream);
        await Chunk(relay, stream.Chunks[0]);
        EnterLeftHost(platform, relay);
        LeaveLeftHost(platform);
        VisitAndLeaveRemote(platform);
        var newer = PullIdTo(relay, "remote");
        await service.HashReads();
        var nonesBefore = relay.Sent.Of(MessageKind.ClipboardPushNone, "remote2").Count;

        clipboard.ReadGate.Hold();
        await Chunks(relay, stream, from: 1);
        await service.Flush();
        clipboard.ReadGate.Release();
        await service.HashReads();
        var whileHeld = new { Hashes = Hashes(relay, "remote2"), None = relay.Sent.Of(MessageKind.ClipboardPushNone, "remote2").Count };
        await AnswerPull(relay, new ClipboardPullResponseMessage(newer, null, Unchanged: true));
        await service.Flush();
        await service.HashReads();

        using (Assert.EnterMultipleScope())
        {
            Assert.That(whileHeld.Hashes, Is.EqualTo([new ClipboardHashMessage(null, Pending: true)]), "pre-condition: told Pending only");
            Assert.That(whileHeld.None, Is.EqualTo(nonesBefore), "the hold released no owed read");
            Assert.That(clipboard.ImagePng, Is.EqualTo(image));
            Assert.That(Hashes(relay, "remote2").Select(h => h.Hash), Is.EqualTo(new ulong?[] { null, ImageHash(image) }));
            Assert.That(relay.Sent.Of(MessageKind.ClipboardPushNone, "remote2"), Has.Count.EqualTo(nonesBefore));
        }
    }

    // as above, the master's clipboard never emptied by the Begin (its clear failed): what waited on the held stream waits
    // on the newer pull, so remote2 is never offered the older clipboard still there, only the held one once it lands
    [Test]
    public async Task ViaTheMaster_TheStreamItWaitsOnIsHeld_TheBeginsClearFailed_ItIsSentOnlyTheHeldClipboard()
    {
        var clipboard = new FakeClipboardSync();
        clipboard.SetupText("old on the master");
        clipboard.ClearFailsValue = true;
        var (platform, relay, service) = await StartSides(clipboard, new FakeTimeProvider());
        VisitAndLeaveRemote(platform);
        var image = BigImage(2);
        var stream = StreamedAnswer(1, new ClipboardPullResponseMessage(PullIdTo(relay, "remote"), null, ImagePng: image));
        await Begin(relay, stream);
        await Chunk(relay, stream.Chunks[0]);
        EnterLeftHost(platform, relay);
        LeaveLeftHost(platform);
        VisitAndLeaveRemote(platform);
        var newer = PullIdTo(relay, "remote");

        await Chunks(relay, stream, from: 1);
        await service.Flush();
        await service.HashReads();
        var whileHeld = new { Hashes = Hashes(relay, "remote2"), clipboard.Text, clipboard.ImagePng };
        clipboard.ClearFailsValue = false;
        await AnswerPull(relay, new ClipboardPullResponseMessage(newer, null, Unchanged: true));
        await service.Flush();
        await service.HashReads();

        using (Assert.EnterMultipleScope())
        {
            Assert.That(whileHeld.Text, Is.EqualTo("old on the master"), "pre-condition: the Begin's clear failed");
            Assert.That(whileHeld.ImagePng, Is.Null, "pre-condition: the stream is held");
            Assert.That(whileHeld.Hashes, Is.EqualTo([new ClipboardHashMessage(null, Pending: true)]), "never the older clipboard while held");
            Assert.That(clipboard.ImagePng, Is.EqualTo(image));
            Assert.That(Hashes(relay, "remote2").Select(h => h.Hash), Is.EqualTo(new ulong?[] { null, ImageHash(image) }));
        }
    }

    // remote2, owed its hash once remote's first stream ends, is due when remote's newer stream replaces it, and its owed
    // read finds the master emptied, so it is owed on that newer stream; the stream is aborted just as it does: remote2 is
    // never left owed on a stream that ended, and is told none follows
    [Test]
    public async Task ViaTheMaster_TheStreamAnOwedHostIsOwedOnEndsAsItIsOwed_ItIsStillTold()
    {
        var clipboard = new FakeClipboardSync();
        clipboard.SetupText("old on the master");
        var (platform, relay, service) = await StartSides(clipboard, new FakeTimeProvider());
        VisitAndLeaveRemote(platform);
        var first = StreamedAnswer(1, new ClipboardPullResponseMessage(PullIdTo(relay, "remote"), null, ImagePng: BigImage(2)));
        await Begin(relay, first);
        await Chunk(relay, first.Chunks[0]);
        EnterLeftHost(platform, relay);
        LeaveLeftHost(platform);
        VisitAndLeaveRemote(platform);
        var owing = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var aborted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        // on the hash read's thread: whether it saw the abort before owing, asserted below
        var abortSeen = false;
        service.OwingOnArriving = () =>
        {
            service.OwingOnArriving = null;
            owing.TrySetResult();
            abortSeen = aborted.Task.Wait(Bound);
        };
        var newer = StreamedAnswer(2, new ClipboardPullResponseMessage(PullIdTo(relay, "remote"), null, ImagePng: BigImage(3)));

        await Begin(relay, newer);
        await owing.Task.WaitAsync(Bound);
        await Abort(relay, newer.Begin.Id);
        aborted.TrySetResult();
        await service.HashReads();
        await service.HashReads();

        using (Assert.EnterMultipleScope())
        {
            Assert.That(abortSeen, Is.True, "pre-condition: the stream ended before the read owed on it");
            Assert.That(Hashes(relay, "remote2"), Is.EqualTo([new ClipboardHashMessage(null, Pending: true)]), "pre-condition: told Pending only");
            Assert.That(relay.Sent.Of(MessageKind.ClipboardPushNone, "remote2"), Has.Count.EqualTo(1), "its wait ended");
        }
    }

    // the newer pull a completed stream is held for is answered by a stream of its own, whose Begin drops the held answer:
    // its deadline goes with it, so passing it never ends remote2's wait early (here, with the Begin's clear failed,
    // offering it the older clipboard); remote2 is sent the newer stream's clipboard once it lands
    [Test]
    public async Task ViaTheMaster_TheHeldForPullStreamsItsOwnAnswer_TheHeldDeadlineNeverEndsTheWaitEarly()
    {
        var clipboard = new FakeClipboardSync();
        clipboard.SetupText("old on the master");
        clipboard.ClearFailsValue = true;
        var clock = new FakeTimeProvider();
        var (platform, relay, service) = await StartSides(clipboard, clock);
        VisitAndLeaveRemote(platform);
        var first = StreamedAnswer(1, new ClipboardPullResponseMessage(PullIdTo(relay, "remote"), null, ImagePng: BigImage(2)));
        await Begin(relay, first);
        await Chunk(relay, first.Chunks[0]);
        EnterLeftHost(platform, relay);
        LeaveLeftHost(platform);
        VisitAndLeaveRemote(platform);
        await Chunks(relay, first, from: 1);
        await service.Flush();
        var image = BigImage(3);
        var newer = StreamedAnswer(2, new ClipboardPullResponseMessage(PullIdTo(relay, "remote"), null, ImagePng: image));

        await Begin(relay, newer);
        await Chunk(relay, newer.Chunks[0]);
        clock.Advance(DeferredClipboardOffer.Wait);
        await service.Flush();
        await service.HashReads();
        var pastTheDeadline = Hashes(relay, "remote2");
        await Chunks(relay, newer, from: 1);
        await service.Flush();
        await service.HashReads();

        using (Assert.EnterMultipleScope())
        {
            Assert.That(pastTheDeadline, Is.EqualTo([new ClipboardHashMessage(null, Pending: true)]), "still waiting on the newer stream");
            Assert.That(clipboard.ImagePng, Is.EqualTo(image));
            Assert.That(Hashes(relay, "remote2").Select(h => h.Hash), Is.EqualTo(new ulong?[] { null, ImageHash(image) }));
            Assert.That(relay.Sent.Of(MessageKind.ClipboardPushNone, "remote2"), Is.Empty);
        }
    }

    // as above, the stream answering the held-for pull fails (its watchdog): the waits moved onto that pull end with it,
    // so remote2, owed on it, is released, never left owed for ever; the Begin's clear failed, so it is offered the
    // clipboard still on the master
    [Test]
    public async Task ViaTheMaster_TheHeldForPullsOwnStreamFails_TheHostOwedOnItIsReleased()
    {
        var clipboard = new FakeClipboardSync();
        clipboard.SetupText("old on the master");
        clipboard.ClearFailsValue = true;
        var clock = new FakeTimeProvider();
        var (platform, relay, service) = await StartSides(clipboard, clock);
        VisitAndLeaveRemote(platform);
        var first = StreamedAnswer(1, new ClipboardPullResponseMessage(PullIdTo(relay, "remote"), null, ImagePng: BigImage(2)));
        await Begin(relay, first);
        await Chunk(relay, first.Chunks[0]);
        EnterLeftHost(platform, relay);
        LeaveLeftHost(platform);
        VisitAndLeaveRemote(platform);
        await Chunks(relay, first, from: 1);
        await service.Flush();
        var newer = StreamedAnswer(2, new ClipboardPullResponseMessage(PullIdTo(relay, "remote"), null, ImagePng: BigImage(3)));
        await Begin(relay, newer);
        await Chunk(relay, newer.Chunks[0]);
        await service.HashReads();
        var whileStreaming = Hashes(relay, "remote2");

        clock.Advance(ClipboardStreamReceiver.Watchdog);
        await service.Flush();
        await service.HashReads();

        using (Assert.EnterMultipleScope())
        {
            Assert.That(whileStreaming, Is.EqualTo([new ClipboardHashMessage(null, Pending: true)]), "pre-condition: owed, waiting");
            Assert.That(Hashes(relay, "remote2").Select(h => h.Hash), Is.EqualTo(new ulong?[] { null, TextHash("old on the master") }),
                "released once the stream failed");
        }
    }

    // the newer pull a completed stream is held for is never answered: the held answer lands once it is waited out, and
    // remote2, told Pending, is sent the clipboard rather than wait for ever
    [Test]
    public async Task ViaTheMaster_TheNewerPullAStreamIsHeldForIsNeverAnswered_ItLandsOnceThatPullIsWaitedOut()
    {
        var clipboard = new FakeClipboardSync();
        clipboard.SetupText("old on the master");
        var clock = new FakeTimeProvider();
        var (platform, relay, service) = await StartSides(clipboard, clock);
        VisitAndLeaveRemote(platform);
        var image = BigImage(2);
        var stream = StreamedAnswer(1, new ClipboardPullResponseMessage(PullIdTo(relay, "remote"), null, ImagePng: image));
        await Begin(relay, stream);
        await Chunk(relay, stream.Chunks[0]);
        EnterLeftHost(platform, relay);
        LeaveLeftHost(platform);
        VisitAndLeaveRemote(platform);

        await Chunks(relay, stream, from: 1);
        await service.Flush();
        await service.HashReads();
        var whileHeld = new { clipboard.ImagePng, Hashes = Hashes(relay, "remote2").Count };
        clock.Advance(DeferredClipboardOffer.Wait);
        await service.Flush();
        await service.HashReads();

        using (Assert.EnterMultipleScope())
        {
            Assert.That(whileHeld.ImagePng, Is.Null, "pre-condition: held");
            Assert.That(whileHeld.Hashes, Is.EqualTo(1), "pre-condition: told Pending only");
            Assert.That(clipboard.ImagePng, Is.EqualTo(image), "landed once the newer pull was waited out");
            Assert.That(Hashes(relay, "remote2").Select(h => h.Hash), Is.EqualTo(new ulong?[] { null, ImageHash(image) }));
            Assert.That(relay.Sent.Of(MessageKind.ClipboardPushNone, "remote2"), Is.Empty);
        }
    }

    // a newer destination replacing its wait leaves it owed its hash, sent once the stream lands
    [Test]
    public async Task ViaTheMaster_ANewerDestinationReplacesItsWait_ItIsStillSentItsHash()
    {
        var clipboard = new FakeClipboardSync();
        clipboard.SetupText("old on the master");
        var (platform, relay, service) = await StartSides(clipboard);
        VisitAndLeaveRemote(platform);
        var image = BigImage(2);
        var stream = StreamedAnswer(1, new ClipboardPullResponseMessage(PullIdTo(relay, "remote"), null, ImagePng: image));
        await Begin(relay, stream);
        await Chunk(relay, stream.Chunks[0]);
        EnterLeftHost(platform, relay);
        LeaveLeftHost(platform);
        TransitionTestHelper.EnterRemote(platform);
        await service.Flush();
        var whileWaiting = Hashes(relay, "remote2");

        await Chunks(relay, stream, from: 1);
        await service.Flush();
        await service.HashReads();

        var hashes = Hashes(relay, "remote2");
        using (Assert.EnterMultipleScope())
        {
            Assert.That(whileWaiting, Is.EqualTo([new ClipboardHashMessage(null, Pending: true)]), "pre-condition: told Pending only");
            Assert.That(hashes, Has.Count.EqualTo(2));
            Assert.That(hashes[^1].Hash, Is.EqualTo(ClipboardUtils.ClipboardHash(new ClipboardSnapshot(null, null, image))));
            Assert.That(clipboard.ImagePng, Is.EqualTo(image));
        }
    }

    // the stream it waited on is aborted, superseded by its host's answer to a newer pull: it is sent that answer's hash
    [Test]
    public async Task ViaTheMaster_TheStreamItWaitedOnIsSupersededByItsHostsNewerAnswer_ItIsSentThatAnswersHash()
    {
        var clipboard = new FakeClipboardSync();
        clipboard.SetupText("old on the master");
        var (platform, relay, service) = await StartSides(clipboard, new FakeTimeProvider());
        VisitAndLeaveRemote(platform);
        var stream = StreamedAnswer(1, new ClipboardPullResponseMessage(PullIdTo(relay, "remote"), null, ImagePng: BigImage(2)));
        await Begin(relay, stream);
        await Chunk(relay, stream.Chunks[0]);
        EnterLeftHost(platform, relay);
        LeaveLeftHost(platform);
        // back onto remote, which waits on remote2's pull, and off it again, which pulls from it once more
        VisitAndLeaveRemote(platform);
        await AnswerPull(relay, new ClipboardPullResponseMessage(PullIdTo(relay, "remote2"), null, Unchanged: true), "remote2");
        var whileOwed = Hashes(relay, "remote2");

        // the user copied on remote since: its stream is aborted for the whole answer to the newer pull
        await Abort(relay, stream.Begin.Id);
        // a read the abort started finds the master's clipboard as the stream's Begin left it
        await service.HashReads();
        await AnswerPull(relay, new ClipboardPullResponseMessage(PullIdTo(relay, "remote"), "copied on remote"));
        await service.Flush();
        await service.HashReads();

        var hashes = Hashes(relay, "remote2");
        using (Assert.EnterMultipleScope())
        {
            Assert.That(whileOwed, Is.EqualTo([new ClipboardHashMessage(null, Pending: true)]), "pre-condition: told Pending only");
            Assert.That(clipboard.Text, Is.EqualTo("copied on remote"), "pre-condition: the newer answer landed");
            Assert.That(hashes.Select(h => h.Hash), Is.EqualTo(new ulong?[] { null, TextHash("copied on remote") }));
            Assert.That(hashes[^1].Visit, Is.Not.Null, "stamped with its visit");
        }
    }

    // -- two pulls outstanding: the newest copy wins, and a pull that finds nothing new cancels no older one --

    [Test]
    public async Task ViaTheMaster_TheOtherHostAnswersUnchangedBeforeTheStreamCompletes_TheMasterGetsTheStreamedCopy()
    {
        var clipboard = new FakeClipboardSync();
        clipboard.SetupText("old on the master");
        var (platform, relay, service) = await StartSides(clipboard);
        var image = BigImage(2);
        var stream = await StreamFromRemoteThenVisitTheLeftHost(platform, relay, image);

        await AnswerPull(relay, new ClipboardPullResponseMessage(PullIdTo(relay, "remote2"), null, Unchanged: true), "remote2");
        await Chunks(relay, stream, from: 1);
        await service.Flush();

        using (Assert.EnterMultipleScope())
        {
            Assert.That(clipboard.ImagePng, Is.EqualTo(image));
            Assert.That(relay.Sent.Of(MessageKind.ClipboardStreamAbort, "remote"), Is.Empty);
        }
    }

    [Test]
    public async Task ViaTheMaster_TheOtherHostAnswersWithANewCopy_ItWinsAndTheOlderStreamIsAborted()
    {
        var clipboard = new FakeClipboardSync();
        clipboard.SetupText("old on the master");
        var (platform, relay, service) = await StartSides(clipboard);
        var stream = await StreamFromRemoteThenVisitTheLeftHost(platform, relay, BigImage(2));

        await AnswerPull(relay, new ClipboardPullResponseMessage(PullIdTo(relay, "remote2"), "copied on remote2"), "remote2");
        var setAfterAnswer = clipboard.SetClipboardCallCount;
        await Chunks(relay, stream, from: 1);
        await service.Flush();

        using (Assert.EnterMultipleScope())
        {
            Assert.That(AbortsTo(relay.Sent, "remote"), Is.EqualTo([1L]));
            Assert.That(clipboard.Text, Is.EqualTo("copied on remote2"));
            Assert.That(clipboard.ImagePng, Is.Null);
            Assert.That(clipboard.SetClipboardCallCount, Is.EqualTo(setAfterAnswer));
        }
    }

    [TestCase(true)]
    [TestCase(false)]
    public async Task ViaTheMaster_BothAnswersStream_TheNewerCopyWinsWhicheverCompletesFirst(bool newerFirst)
    {
        var clipboard = new FakeClipboardSync();
        clipboard.SetupText("old on the master");
        var (platform, relay, service) = await StartSides(clipboard);
        var older = await StreamFromRemoteThenVisitTheLeftHost(platform, relay, BigImage(2));
        var image = BigImage(3);
        var newer = StreamedAnswer(7, new ClipboardPullResponseMessage(PullIdTo(relay, "remote2"), null, ImagePng: image));
        await Begin(relay, newer, "remote2");

        if (newerFirst)
        {
            await Chunks(relay, newer, host: "remote2");
            await Chunks(relay, older, from: 1);
        }
        else
        {
            await Chunks(relay, older, from: 1);
            await Chunks(relay, newer, host: "remote2");
        }
        await service.Flush();

        var aborted = AbortsTo(relay.Sent, "remote");
        using (Assert.EnterMultipleScope())
        {
            Assert.That(clipboard.ImagePng, Is.EqualTo(image));
            // the newer answer's Begin superseded the older pull, aborting its stream whichever completes first
            Assert.That(aborted, Is.EqualTo([1L]));
            Assert.That(relay.Sent.Of(MessageKind.ClipboardStreamAbort, "remote2"), Is.Empty);
        }
    }

    [TestCase(true)]
    [TestCase(false)]
    public async Task Crossing_TwoPullsOutstanding_TheNextHostGetsPendingThenTheRealHashOnceBothResolve(bool streamCompletesFirst)
    {
        var clipboard = new FakeClipboardSync();
        clipboard.SetupText("old on the master");
        var (platform, relay, service) = await Services.StartOnline(
            TransitionTestHelper.CreateService(profile: Chain3Config, clipboard: clipboard, timeProvider: new FakeTimeProvider()),
            "remote", "remote2", "remote3");
        TransitionTestHelper.EnterRemote(platform);
        TransitionTestHelper.MoveRightTo(platform, relay, "remote2");
        var image = BigImage(2);
        var stream = StreamedAnswer(1, new ClipboardPullResponseMessage(PullIdTo(relay, "remote"), null, ImagePng: image));
        await Begin(relay, stream);
        await Chunk(relay, stream.Chunks[0]);
        TransitionTestHelper.MoveRightTo(platform, relay, "remote3");
        var atEntry = Hashes(relay, "remote3");
        var unchanged = new ClipboardPullResponseMessage(PullIdTo(relay, "remote2"), null, Unchanged: true);

        if (streamCompletesFirst) await Chunks(relay, stream, from: 1);
        else await AnswerPull(relay, unchanged, "remote2");
        await service.HashReads();
        var afterFirst = Hashes(relay, "remote3");
        if (streamCompletesFirst) await AnswerPull(relay, unchanged, "remote2");
        else await Chunks(relay, stream, from: 1);
        await relay.Sent.WaitFor(MessageKind.ClipboardHash, 2, "remote3");
        await service.HashReads();

        var hashes = Hashes(relay, "remote3");
        using (Assert.EnterMultipleScope())
        {
            Assert.That(atEntry, Is.EqualTo([new ClipboardHashMessage(null, Pending: true)]));
            Assert.That(afterFirst, Is.EqualTo(atEntry));
            Assert.That(hashes, Has.Count.EqualTo(2));
            Assert.That(hashes[^1].Hash, Is.EqualTo(ClipboardUtils.ClipboardHash(new ClipboardSnapshot(null, null, image))));
            Assert.That(hashes[^1].Pending, Is.Null);
            Assert.That(clipboard.ImagePng, Is.EqualTo(image));
        }
    }

    [Test]
    public async Task ViaTheMaster_UserCopiesOnTheMasterMidStream_TheOtherHostGetsThatCopysHashOnce()
    {
        var clipboard = new FakeClipboardSync();
        clipboard.SetupText("old on the master");
        var (platform, relay, service) = await StartSides(clipboard);
        VisitAndLeaveRemote(platform);
        var stream = StreamedAnswer(1, new ClipboardPullResponseMessage(PullIdTo(relay, "remote"), null, ImagePng: BigImage(2)));
        await Begin(relay, stream);
        await Chunk(relay, stream.Chunks[0]);
        EnterLeftHost(platform, relay);

        clipboard.CanUseEchoFallbackValue = false;
        clipboard.SetupText("copied here");
        await Chunks(relay, stream, from: 1);
        await service.Flush();
        await relay.Sent.WaitFor(MessageKind.ClipboardHash, 2, "remote2");
        await service.HashReads();

        var hashes = Hashes(relay, "remote2");
        using (Assert.EnterMultipleScope())
        {
            Assert.That(AbortsTo(relay.Sent, "remote"), Is.EqualTo([1L]));
            Assert.That(clipboard.Text, Is.EqualTo("copied here"));
            Assert.That(hashes.Select(h => h.Pending), Is.EqualTo(new bool?[] { true, null }));
            Assert.That(hashes.Select(h => h.Hash), Is.EqualTo(new ulong?[] { null, TextHash("copied here") }));
        }
    }

    // -- a copy the user makes on a host told Pending wins over the clipboard on its way --

    [Test]
    public async Task ViaTheMaster_UserCopiesOnTheOtherHostAfterPending_ItsCopyWinsAndTheStreamIsAborted()
    {
        var clipboard = new FakeClipboardSync();
        clipboard.SetupText("old on the master");
        var (platform, relay, service) = await StartSides(clipboard);
        VisitAndLeaveRemote(platform);
        var image = BigImage(2);
        var stream = StreamedAnswer(1, new ClipboardPullResponseMessage(PullIdTo(relay, "remote"), null, ImagePng: image));
        await Begin(relay, stream);
        await Chunk(relay, stream.Chunks[0]);
        EnterLeftHost(platform, relay);
        await Chunks(relay, stream, from: 1, to: stream.Chunks.Length - 1);

        // remote2 empties for Pending, the user copies there, and it asks for no push
        LeaveLeftHost(platform);
        await AnswerPull(relay, new ClipboardPullResponseMessage(PullIdTo(relay, "remote2"), "copied on remote2"), "remote2");
        var setAfterAnswer = clipboard.SetClipboardCallCount;
        await Chunk(relay, stream.Chunks[^1]);
        await service.Flush();
        await service.HashReads();

        using (Assert.EnterMultipleScope())
        {
            // told Pending, it is owed its hash once its wait ends: its own copy's, which it matches, so no push follows
            Assert.That(Hashes(relay, "remote2").Select(h => h.Hash), Is.EqualTo(new ulong?[] { null, TextHash("copied on remote2") }));
            Assert.That(Hashes(relay, "remote2")[0], Is.EqualTo(new ClipboardHashMessage(null, Pending: true)));
            Assert.That(relay.Sent.Of(MessageKind.ClipboardPush, "remote2"), Is.Empty);
            Assert.That(relay.Sent.Of(MessageKind.ClipboardStreamBegin, "remote2"), Is.Empty);
            Assert.That(AbortsTo(relay.Sent, "remote"), Is.EqualTo([1L]));
            Assert.That(clipboard.Text, Is.EqualTo("copied on remote2"));
            Assert.That(clipboard.ImagePng, Is.Null);
            Assert.That(clipboard.SetClipboardCallCount, Is.EqualTo(setAfterAnswer));
        }
    }

    // the master's baseline is stale (its entry hash to remote2 deferred, so never read), yet the user's newest copy,
    // made on remote2 after the one on the master, wins
    [Test]
    public async Task ViaTheMaster_CopiedOnTheMasterThenOnTheDeferredHost_TheNewerCopyWins()
    {
        var clipboard = new FakeClipboardSync();
        clipboard.SetupText("old on the master");
        var (platform, relay, service) = await StartSides(clipboard);
        VisitAndLeaveRemote(platform);
        var stream = StreamedAnswer(1, new ClipboardPullResponseMessage(PullIdTo(relay, "remote"), null, ImagePng: BigImage(2)));
        await Begin(relay, stream);
        await Chunk(relay, stream.Chunks[0]);
        clipboard.CanUseEchoFallbackValue = false;
        clipboard.SetupText("y");

        EnterLeftHost(platform, relay);
        LeaveLeftHost(platform);
        await AnswerPull(relay, new ClipboardPullResponseMessage(PullIdTo(relay, "remote2"), "z"), "remote2");
        await service.Flush();

        using (Assert.EnterMultipleScope())
        {
            Assert.That(Hashes(relay, "remote2").Select(h => h.Hash), Has.None.EqualTo(TextHash("y")), "pre-condition: its entry hash was never read");
            Assert.That(clipboard.Text, Is.EqualTo("z"));
        }
    }

    [Test]
    public async Task ViaTheMaster_OnThroughAnEmptyHostToAFourth_ItGetsPendingThenTheStreamedCopysHashOnce()
    {
        var clipboard = new FakeClipboardSync();
        clipboard.SetupText("old on the master");
        var (platform, relay, service) = await StartQuad(clipboard);
        var image = BigImage(2);
        var stream = await StreamFromRemoteThenOnToTheFourth(platform, relay, image);
        var atEntry = Hashes(relay, "remote3");
        var toLeftHost = SentTo(relay, "remote2").Count;

        await AnswerPull(relay, new ClipboardPullResponseMessage(PullIdTo(relay, "remote2"), null, Unchanged: true), "remote2");
        await service.HashReads();
        var afterUnchanged = Hashes(relay, "remote3");
        await Chunks(relay, stream, from: 1);
        await service.Flush();
        await relay.Sent.WaitFor(MessageKind.ClipboardHash, 2, "remote3");
        await service.HashReads();

        var hashes = Hashes(relay, "remote3");
        using (Assert.EnterMultipleScope())
        {
            Assert.That(atEntry, Is.EqualTo([new ClipboardHashMessage(null, Pending: true)]));
            Assert.That(afterUnchanged, Is.EqualTo(atEntry));
            Assert.That(hashes, Has.Count.EqualTo(2));
            Assert.That(hashes[^1].Hash, Is.EqualTo(ClipboardUtils.ClipboardHash(new ClipboardSnapshot(null, null, image))));
            Assert.That(hashes[^1].Pending, Is.Null);
            Assert.That(clipboard.ImagePng, Is.EqualTo(image));
            // told Pending, its wait replaced by remote3's, it is owed its hash: one hash more, of the streamed copy
            Assert.That(SentTo(relay, "remote2"), Has.Count.EqualTo(toLeftHost + 1));
            Assert.That(Hashes(relay, "remote2")[^1].Hash, Is.EqualTo(ClipboardUtils.ClipboardHash(new ClipboardSnapshot(null, null, image))));
            Assert.That(relay.Sent.Of(MessageKind.ClipboardStreamAbort, "remote"), Is.Empty);
        }
    }

    [Test]
    public async Task ViaTheMaster_OnThroughAHostWithAUserCopyToAFourth_ThatCopyWinsAndItsHashGoesOnce()
    {
        var clipboard = new FakeClipboardSync();
        clipboard.SetupText("old on the master");
        var (platform, relay, service) = await StartQuad(clipboard);
        var stream = await StreamFromRemoteThenOnToTheFourth(platform, relay, BigImage(2));
        var atEntry = Hashes(relay, "remote3");
        var toLeftHost = SentTo(relay, "remote2").Count;

        await AnswerPull(relay, new ClipboardPullResponseMessage(PullIdTo(relay, "remote2"), "copied on remote2"), "remote2");
        await Chunks(relay, stream, from: 1);
        await service.Flush();
        await relay.Sent.WaitFor(MessageKind.ClipboardHash, 2, "remote3");
        await service.HashReads();

        var hashes = Hashes(relay, "remote3");
        using (Assert.EnterMultipleScope())
        {
            Assert.That(atEntry, Is.EqualTo([new ClipboardHashMessage(null, Pending: true)]));
            Assert.That(AbortsTo(relay.Sent, "remote"), Is.EqualTo([1L]));
            Assert.That(clipboard.Text, Is.EqualTo("copied on remote2"));
            Assert.That(clipboard.ImagePng, Is.Null);
            Assert.That(hashes.Select(h => h.Pending), Is.EqualTo(new bool?[] { true, null }));
            Assert.That(hashes.Select(h => h.Hash), Is.EqualTo(new ulong?[] { null, TextHash("copied on remote2") }));
            // told Pending, its wait replaced by remote3's, it is owed its hash: its own copy's, which it matches
            Assert.That(SentTo(relay, "remote2"), Has.Count.EqualTo(toLeftHost + 1));
            Assert.That(Hashes(relay, "remote2")[^1].Hash, Is.EqualTo(TextHash("copied on remote2")));
        }
    }

    // -- a push still streaming the master's old clipboard is aborted once a newer copy lands on the master --

    [Test]
    public async Task PushStreamingToALeftHost_ACopyFromTheNextHostLands_ThePushIsAbortedAndTheNewCopyOffered()
    {
        var clipboard = new FakeClipboardSync();
        clipboard.SetupImage(BigImage(1));
        var (platform, relay, service) = await StartQuad(clipboard);
        var old = await PushStreamingToTheLeftHostThenOnToTheFourthAndBack(platform, relay);
        var atReentry = Hashes(relay, "remote2").Count;
        var copy = BigImage(3);

        await AnswerPull(relay, new ClipboardPullResponseMessage(PullIdTo(relay, "remote3"), null, ImagePng: copy), "remote3");
        await service.HashReads();
        var aborted = AbortsTo(relay.Sent, "remote2");
        relay.Reliable.Release();
        await SimulatePullRequest(relay, "remote2");
        await relay.Sent.WaitFor(MessageKind.ClipboardStreamBegin, 2, "remote2");
        var push = await SentPush(relay, "remote2");

        using (Assert.EnterMultipleScope())
        {
            Assert.That(aborted, Is.EqualTo([old]));
            Assert.That(relay.Sent.Of<ClipboardStreamChunkMessage>(MessageKind.ClipboardStreamChunk, "remote2").Select(c => c.Id), Has.None.EqualTo(old));
            Assert.That(Hashes(relay, "remote2").Skip(atReentry).Select(h => h.Hash),
                Is.EqualTo([ClipboardUtils.ClipboardHash(new ClipboardSnapshot(null, null, copy))]));
            Assert.That(push.ImagePng, Is.EqualTo(copy));
            Assert.That(relay.Sent.Of(MessageKind.ClipboardPush, "remote2"), Is.Empty);
        }
    }

    // the cursor gone elsewhere, the host whose push of the old clipboard was aborted is sent the new one's hash
    [Test]
    public async Task PushStreamingToAHostTheCursorLeft_ACopyFromAnotherHostLands_ThatHostIsSentTheNewHash()
    {
        var clipboard = new FakeClipboardSync();
        clipboard.SetupImage(BigImage(1));
        var (platform, relay, service) = await StartSides(clipboard, new FakeTimeProvider());
        EnterLeftHost(platform, relay);
        relay.Reliable.Hold();
        await SimulatePullRequest(relay, "remote2");
        await relay.Sent.WaitFor(MessageKind.ClipboardStreamBegin, 1, "remote2");
        var old = relay.Sent.Of<ClipboardStreamBeginMessage>(MessageKind.ClipboardStreamBegin, "remote2").Single().Id;
        LeaveLeftHost(platform);
        await AnswerPull(relay, new ClipboardPullResponseMessage(PullIdTo(relay, "remote2"), null, Unchanged: true), "remote2");
        VisitAndLeaveRemote(platform);
        var before = Hashes(relay, "remote2");

        await AnswerPull(relay, new ClipboardPullResponseMessage(PullIdTo(relay, "remote"), "copied on remote"));
        relay.Reliable.Release();
        await service.Flush();
        await service.HashReads();

        var hashes = Hashes(relay, "remote2");
        using (Assert.EnterMultipleScope())
        {
            Assert.That(platform.IsOnVirtualScreen, Is.False, "pre-condition: the cursor is on the master's screen");
            Assert.That(clipboard.Text, Is.EqualTo("copied on remote"), "pre-condition: the copy landed");
            Assert.That(AbortsTo(relay.Sent, "remote2"), Is.EqualTo([old]), "the old push is aborted");
            Assert.That(hashes.Skip(before.Count).Select(h => h.Hash), Is.EqualTo(new ulong?[] { TextHash("copied on remote") }));
            Assert.That(hashes[^1].Visit, Is.EqualTo(before[^1].Visit), "stamped with its latest visit");
        }
    }

    // its owed read finding nothing to offer, the host whose push was aborted is told none follows, for its latest visit
    [Test]
    public async Task PushStreamingToAHostTheCursorLeft_ACopyLandsButTheOwedReadFindsNothing_ThatHostIsToldNoPushFollows()
    {
        var clipboard = new FakeClipboardSync();
        clipboard.SetupImage(BigImage(1));
        var (platform, relay, service) = await StartSides(clipboard, new FakeTimeProvider());
        EnterLeftHost(platform, relay);
        // its entry hash sent, which the latest visit is read from
        await service.HashReads();
        relay.Reliable.Hold();
        await SimulatePullRequest(relay, "remote2");
        await relay.Sent.WaitFor(MessageKind.ClipboardStreamBegin, 1, "remote2");
        LeaveLeftHost(platform);
        await AnswerPull(relay, new ClipboardPullResponseMessage(PullIdTo(relay, "remote2"), null, Unchanged: true), "remote2");
        VisitAndLeaveRemote(platform);
        await service.HashReads();
        var before = Hashes(relay, "remote2");
        var nones = relay.Sent.Of(MessageKind.ClipboardPushNone, "remote2").Count;
        // the copy lands, but the clipboard can no longer be read for its hash
        clipboard.ReadFailsValue = true;

        await AnswerPull(relay, new ClipboardPullResponseMessage(PullIdTo(relay, "remote"), "copied on remote"));
        relay.Reliable.Release();
        await service.Flush();
        await service.HashReads();

        using (Assert.EnterMultipleScope())
        {
            Assert.That(clipboard.Text, Is.EqualTo("copied on remote"), "pre-condition: the copy landed");
            Assert.That(AbortsTo(relay.Sent, "remote2"), Has.Count.EqualTo(1), "pre-condition: the old push is aborted");
            Assert.That(Hashes(relay, "remote2"), Has.Count.EqualTo(before.Count), "no hash to offer");
            Assert.That(relay.Sent.Of<ClipboardPushNoneMessage>(MessageKind.ClipboardPushNone, "remote2").Skip(nones).Select(n => n.Visit),
                Is.EqualTo([before[^1].Visit]), "told no push follows, for its latest visit");
        }
    }

    // its push of the old clipboard aborted while its entry hash still waits on another pull, the cursor's host is owed
    // the new clipboard's hash: sent once that pull is answered, though the cursor left it meanwhile
    [Test]
    public async Task PushStreamingToTheCursorsHostWhileItsHashWaits_ACopyLandsAndTheCursorLeaves_ItIsSentTheNewHashOnceTheWaitEnds()
    {
        var clipboard = new FakeClipboardSync();
        clipboard.SetupImage(BigImage(1));
        var (platform, relay, service) = await StartQuad(clipboard);
        // a pull to remote outstanding, which the left host's entries wait on beside remote3's
        VisitAndLeaveRemote(platform);
        var old = await PushStreamingToTheLeftHostThenOnToTheFourthAndBack(platform, relay);
        var atReentry = Hashes(relay, "remote2").Count;

        await AnswerPull(relay, new ClipboardPullResponseMessage(PullIdTo(relay, "remote"), "copied on remote"));
        await service.HashReads();
        var whileWaiting = Hashes(relay, "remote2").Count;
        // entered from remote3, so back across its right edge onto the master's screen
        for (var i = 0; i < 40 && platform.IsOnVirtualScreen; i++)
            platform.FireMouseDelta(100, 0);
        await AnswerUnchanged(relay, "remote3");
        relay.Reliable.Release();
        await service.Flush();
        await service.HashReads();

        var hashes = Hashes(relay, "remote2");
        using (Assert.EnterMultipleScope())
        {
            Assert.That(platform.IsOnVirtualScreen, Is.False, "pre-condition: the cursor left remote2 for the master's screen");
            Assert.That(clipboard.Text, Is.EqualTo("copied on remote"), "pre-condition: the copy landed");
            Assert.That(AbortsTo(relay.Sent, "remote2"), Is.EqualTo([old]), "pre-condition: the old push is aborted");
            Assert.That(whileWaiting, Is.EqualTo(atReentry), "pre-condition: its hash waits on remote3's pull");
            Assert.That(hashes.Skip(atReentry).Select(h => h.Hash), Is.EqualTo(new ulong?[] { TextHash("copied on remote") }));
        }
    }

    // its push of the old clipboard aborted while its hash waits on nothing, the cursor's host is owed the new one's hash:
    // sent though the cursor leaves it mid-read
    [Test]
    public async Task PushStreamingToTheCursorsHost_ACopyLandsAndTheCursorLeavesMidRead_ItIsStillSentTheNewHash()
    {
        var clipboard = new FakeClipboardSync();
        clipboard.SetupImage(BigImage(1));
        var (platform, relay, service) = await StartQuad(clipboard);
        var old = await PushStreamingToTheLeftHostThenOnToTheFourthAndBack(platform, relay);
        var atReentry = Hashes(relay, "remote2").Count;
        // the fire callback flushes the actor alone, never waiting on the parked read
        platform.AfterFireCallback = service.Flush;
        clipboard.ReadGate.Hold();

        await AnswerPull(relay, new ClipboardPullResponseMessage(PullIdTo(relay, "remote3"), "copied on remote3"), "remote3");
        await clipboard.ReadsParked.Next("remote2's hash read");
        // entered from remote3, so back across its right edge onto the master's screen
        for (var i = 0; i < 40 && platform.IsOnVirtualScreen; i++)
            platform.FireMouseDelta(100, 0);
        clipboard.ReadGate.Release();
        relay.Reliable.Release();
        await service.Flush();
        await service.HashReads();

        using (Assert.EnterMultipleScope())
        {
            Assert.That(platform.IsOnVirtualScreen, Is.False, "pre-condition: the cursor left remote2 mid-read");
            Assert.That(clipboard.Text, Is.EqualTo("copied on remote3"), "pre-condition: the copy landed");
            Assert.That(AbortsTo(relay.Sent, "remote2"), Is.EqualTo([old]), "pre-condition: the old push is aborted");
            Assert.That(Hashes(relay, "remote2").Skip(atReentry).Select(h => h.Hash), Is.EqualTo(new ulong?[] { TextHash("copied on remote3") }));
        }
    }

    [Test]
    public async Task PushStreamingToALeftHost_TheNextHostAnswersUnchanged_ThePushRunsOn()
    {
        var clipboard = new FakeClipboardSync();
        var image = BigImage(1);
        clipboard.SetupImage(image);
        var (platform, relay, _) = await StartQuad(clipboard);
        var old = await PushStreamingToTheLeftHostThenOnToTheFourthAndBack(platform, relay);

        await AnswerPull(relay, new ClipboardPullResponseMessage(PullIdTo(relay, "remote3"), null, Unchanged: true), "remote3");
        var aborted = relay.Sent.Of(MessageKind.ClipboardStreamAbort);
        relay.Reliable.Release();
        var push = await SentPush(relay, "remote2");

        using (Assert.EnterMultipleScope())
        {
            Assert.That(aborted, Is.Empty);
            Assert.That(relay.Sent.Of<ClipboardStreamBeginMessage>(MessageKind.ClipboardStreamBegin, "remote2").Select(b => b.Id), Is.EqualTo([old]));
            Assert.That(push.ImagePng, Is.EqualTo(image));
        }
    }

    // -- helpers --

    // the master's big clipboard streaming to remote2, held at its Begin, while the cursor goes on to remote3 (remote2
    // answering its pull Unchanged) and back onto remote2, which pulls from remote3: the stream's id
    private static async Task<long> PushStreamingToTheLeftHostThenOnToTheFourthAndBack(FakePlatform platform, FakeRelay relay)
    {
        EnterLeftHost(platform, relay);
        relay.Reliable.Hold();
        await SimulatePullRequest(relay, "remote2");
        await relay.Sent.WaitFor(MessageKind.ClipboardStreamBegin, 1, "remote2");
        var id = relay.Sent.Of<ClipboardStreamBeginMessage>(MessageKind.ClipboardStreamBegin, "remote2").Single().Id;

        MoveTo(platform, relay, "remote3", -100, 1);
        await AnswerPull(relay, new ClipboardPullResponseMessage(PullIdTo(relay, "remote2"), null, Unchanged: true), "remote2");
        MoveTo(platform, relay, "remote2", 100, 2);
        Assert.That(relay.Sent.Of(MessageKind.ClipboardPull, "remote3"), Has.Count.EqualTo(1), "pre-condition: pulled from remote3");
        return id;
    }

    // pushes the cursor by dx until host has been entered the given number of times
    private static void MoveTo(FakePlatform platform, FakeRelay relay, string host, int dx, int entries)
    {
        bool Entered() => relay.Sent.Of(MessageKind.EnterScreen, host).Count >= entries;
        for (var i = 0; i < 40 && !Entered(); i++)
            platform.FireMouseDelta(dx, 0);
        Assert.That(Entered(), Is.True, $"pre-condition: on {host}");
    }
    // remote3 ← remote2 ← home → remote, all online
    private static readonly IHydraProfile QuadConfig = TransitionTestHelper.Profile("home", new HydraConfig
    {
        Mode = Mode.Master,
        Hosts =
        [
            Middle("home", "remote2", "remote"),
            new HostConfig { Name = "remote", Neighbours = [new NeighbourConfig { Direction = Direction.Left, Name = "home" }] },
            Middle("remote2", "remote3", "home"),
            new HostConfig { Name = "remote3", Neighbours = [new NeighbourConfig { Direction = Direction.Right, Name = "remote2" }] },
        ],
    });

    private Task<TestServiceBundle> StartQuad(FakeClipboardSync clipboard) =>
        Services.StartOnline(TransitionTestHelper.CreateService(profile: QuadConfig, clipboard: clipboard, timeProvider: new FakeTimeProvider()),
            "remote", "remote2", "remote3");

    // remote's answer streaming in (Begin and a chunk, stream 1) when the cursor goes onto remote2 and on to remote3,
    // which pulls from remote2: remote's stream
    private static async Task<StreamedClipboard> StreamFromRemoteThenOnToTheFourth(FakePlatform platform, FakeRelay relay, byte[] image)
    {
        VisitAndLeaveRemote(platform);
        var stream = StreamedAnswer(1, new ClipboardPullResponseMessage(PullIdTo(relay, "remote"), null, ImagePng: image));
        await Begin(relay, stream);
        await Chunk(relay, stream.Chunks[0]);
        EnterLeftHost(platform, relay);
        bool Entered() => relay.Sent.Of(MessageKind.EnterScreen, "remote3").Count > 0;
        for (var i = 0; i < 40 && !Entered(); i++)
            platform.FireMouseDelta(-100, 0);
        using (Assert.EnterMultipleScope())
        {
            Assert.That(Entered(), Is.True, "pre-condition: on remote3");
            Assert.That(relay.Sent.Of(MessageKind.ClipboardPull, "remote2"), Has.Count.EqualTo(1), "pre-condition: pulled from remote2");
        }
        return stream;
    }

    // home → remote → remote2 → remote3
    private static readonly IHydraProfile Chain3Config = TransitionTestHelper.Profile("home", new HydraConfig
    {
        Mode = Mode.Master,
        Hosts =
        [
            new HostConfig { Name = "home", Neighbours = [new NeighbourConfig { Direction = Direction.Right, Name = "remote" }] },
            Middle("remote", "home", "remote2"),
            Middle("remote2", "remote", "remote3"),
            new HostConfig { Name = "remote3", Neighbours = [new NeighbourConfig { Direction = Direction.Left, Name = "remote2" }] },
        ],
    });

    private static async Task Depart(FakeRelay relay, bool disconnect)
    {
        if (disconnect) await relay.FireDisconnected();
        else await relay.FirePeersChanged();
    }
}

// holds the first stream timeout's warning, logged on the timer thread between the receiver dropping the stream and
// raising its failure, until the test releases it
internal sealed class HeldOnTimeoutLog : ILogger<InputRouter>
{
    private int _held;

    public Gate Gate { get; } = new(held: true);
    public TaskCompletionSource Reached { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

    public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
    public bool IsEnabled(LogLevel logLevel) => logLevel >= LogLevel.Warning;

    public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
    {
        if (!formatter(state, exception).Contains("timed out") || Interlocked.Exchange(ref _held, 1) != 0) return;
        Reached.TrySetResult();
        Assert.That(Gate.Passed().Wait(Bound), Is.True, "the timeout's warning was let go");
    }
}

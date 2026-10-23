using Cathedral.Extensions;
using Hydra.Platform;
using Hydra.Relay;
using Microsoft.Extensions.Time.Testing;
using Tests.Setup;
using static Tests.Setup.ClipboardStreaming;

namespace Tests.Relay.Edge;

// concurrent copies: the newest copy wins, so a copy the user makes anywhere while a clipboard is on its way outranks it,
// and no host pastes anything older than the newest copy it has been told about
[TestFixture]
public class ClipboardEdgeCopyTests : MasterClipboardFixture
{
    private readonly SlaveRelays _slaves = new();

    [TearDown]
    public Task StopSlaves() => _slaves.StopAll();

    // -- 1. a copy on the master while it streams a push to A --

    [Test]
    public async Task CopyOnTheMasterWhilePushingToA_TheNextOfferAndPushCarryTheCopy_AndTheStaleStreamIsAbortedFirst()
    {
        var clipboard = new FakeClipboardSync();
        clipboard.SetupImage(BigImage(1));
        var (platform, relay, _) = await StartMaster(clipboard);
        TransitionTestHelper.EnterRemote(platform);
        relay.Reliable.Hold();
        await SimulatePullRequest(relay);
        await relay.Sent.WaitFor(MessageKind.ClipboardStreamBegin, 1, "remote");
        var stale = relay.Sent.Of<ClipboardStreamBeginMessage>(MessageKind.ClipboardStreamBegin, "remote").Single().Id;

        var copy = BigImage(2);
        clipboard.CanUseEchoFallbackValue = false;
        clipboard.SetupImage(copy);
        // off the remote, which answers from its emptied clipboard, and back on
        LeaveRemote(platform);
        Assert.That(platform.IsOnVirtualScreen, Is.False, "pre-condition: back on the master's screen");
        await AnswerPull(relay, new ClipboardPullResponseMessage(PullIdTo(relay), null));
        TransitionTestHelper.EnterRemote(platform);
        await relay.Sent.WaitFor(MessageKind.ClipboardHash, 2, "remote");
        var offered = Hashes(relay, "remote");
        await SimulatePullRequest(relay);
        relay.Reliable.Release();
        var push = await SentPush(relay, "remote", begins: 2);

        var sent = relay.Sent.Snapshot();
        var fresh = relay.Sent.Of<ClipboardStreamBeginMessage>(MessageKind.ClipboardStreamBegin, "remote").Last().Id;
        using (Assert.EnterMultipleScope())
        {
            Assert.That(offered[^1].Hash, Is.EqualTo(ImageHash(copy)));
            Assert.That(push.ImagePng, Is.EqualTo(copy));
            Assert.That(clipboard.ImagePng, Is.EqualTo(copy));
            Assert.That(AbortsTo(relay.Sent, "remote"), Is.EqualTo([stale]));
            Assert.That(IndexOf<ClipboardStreamAbortMessage>(sent, MessageKind.ClipboardStreamAbort, "remote", a => a.Id == stale),
                Is.LessThan(IndexOf<ClipboardStreamBeginMessage>(sent, MessageKind.ClipboardStreamBegin, "remote", b => b.Id == fresh)));
            Assert.That(relay.Sent.Of<ClipboardStreamChunkMessage>(MessageKind.ClipboardStreamChunk).Select(c => c.Id), Has.None.EqualTo(stale));
        }
    }

    // -- 2. a copy on A while it receives a stream from the master --

    [TestCase(false)]
    [TestCase(true)]
    public async Task UserCopiesOnAMidStream_AKeepsItAbortsAndItsNextPullAnswerCarriesTheCopy(bool pullBeforeTheNextChunk)
    {
        var clipboard = new FakeClipboardSync();
        var slave = Slave(clipboard);
        var stream = HashedPush(5, new ClipboardPushMessage(BigText('a')));
        await Begin(slave, stream);
        await Chunk(slave, stream.Chunks[0]);

        clipboard.CanUseEchoFallbackValue = false;
        clipboard.SetupText("copied on A");
        // the master pulls with the hash of what it is streaming
        var pull = new ClipboardPullMessage(8, stream.Begin.ContentHash);
        if (pullBeforeTheNextChunk) await slave.SimulateReceive(Master, MessageKind.ClipboardPull, pull.ToSaneJson());
        await Chunks(slave, stream, from: 1);
        if (!pullBeforeTheNextChunk) await slave.SimulateReceive(Master, MessageKind.ClipboardPull, pull.ToSaneJson());

        var answer = slave.Sent.Of<ClipboardPullResponseMessage>(MessageKind.ClipboardPullResponse, Master).Single();
        using (Assert.EnterMultipleScope())
        {
            Assert.That(AbortsTo(slave.Sent, Master), Is.EqualTo([5L]));
            Assert.That(clipboard.SetClipboardCallCount, Is.Zero);
            Assert.That(clipboard.Text, Is.EqualTo("copied on A"));
            Assert.That(answer.Unchanged, Is.Null);
            Assert.That(answer.Text, Is.EqualTo("copied on A"));
        }
    }

    // -- 3. a copy on C after it was told Pending, while A's answer streams --

    [TestCase(false)]
    [TestCase(true)]
    public async Task UserCopiesOnCAfterPending_LeavingCTheMasterTakesThatCopy_AndAbortsAsStream(bool aCompletesWhileOnC)
    {
        var clipboard = new FakeClipboardSync();
        clipboard.SetupText("old on the master");
        var (platform, relay, service) = await StartSides(clipboard);
        VisitAndLeaveRemote(platform);
        var fromA = BigImage(2);
        var stream = StreamedAnswer(1, new ClipboardPullResponseMessage(PullIdTo(relay, "remote"), null, ImagePng: fromA));
        await Begin(relay, stream);
        await Chunk(relay, stream.Chunks[0]);
        EnterLeftHost(platform, relay);
        var told = Hashes(relay, "remote2");

        if (aCompletesWhileOnC) await Chunks(relay, stream, from: 1);
        // the user copies on C, so leaving it answers that copy
        LeaveLeftHost(platform);
        var pull = relay.Sent.Of<ClipboardPullMessage>(MessageKind.ClipboardPull, "remote2").Single();
        await AnswerPull(relay, new ClipboardPullResponseMessage(pull.PullId, "copied on C"), "remote2");
        var setAfterAnswer = clipboard.SetClipboardCallCount;
        if (!aCompletesWhileOnC) await Chunks(relay, stream, from: 1);
        await service.Flush();
        await service.HashReads();

        var aborted = AbortsTo(relay.Sent, "remote");
        using (Assert.EnterMultipleScope())
        {
            Assert.That(told, Is.EqualTo([new ClipboardHashMessage(null, Pending: true)]));
            Assert.That(clipboard.Text, Is.EqualTo("copied on C"));
            Assert.That(clipboard.ImagePng, Is.Null);
            Assert.That(clipboard.SetClipboardCallCount, Is.EqualTo(setAfterAnswer));
            Assert.That(aborted, aCompletesWhileOnC ? Is.Empty : Is.EqualTo([1L]));
            Assert.That(relay.Sent.Of(MessageKind.ClipboardStreamAbort, "remote2"), Is.Empty);
        }
    }

    [Test]
    public async Task UserCopiesOnCAfterPending_TheLateRealHashLeavesTheCopy_AndThePullAnswersIt()
    {
        var clipboard = new FakeClipboardSync();
        clipboard.SetupText("old on C");
        var slave = Slave(clipboard);
        await slave.SimulateReceive(Master, MessageKind.ClipboardHash, new ClipboardHashMessage(null, Pending: true).ToSaneJson());
        var emptied = clipboard.Text;

        clipboard.CanUseEchoFallbackValue = false;
        clipboard.SetupText("copied on C");
        // A's copy lands on the master, which offers it to C
        var fromA = BigImage(2);
        var landed = ClipboardWire.ToHash(new ClipboardSnapshot(null, null, fromA));
        await slave.SimulateReceive(Master, MessageKind.ClipboardHash, landed.ToSaneJson());
        await PushIfRequested(slave, new ClipboardPushMessage("", ImagePng: fromA));
        await slave.SimulateReceive(Master, MessageKind.ClipboardPull, new ClipboardPullMessage(9, landed.Hash).ToSaneJson());

        var answer = slave.Sent.Of<ClipboardPullResponseMessage>(MessageKind.ClipboardPullResponse, Master).Single();
        using (Assert.EnterMultipleScope())
        {
            Assert.That(emptied, Is.Null);
            Assert.That(clipboard.Text, Is.EqualTo("copied on C"));
            Assert.That(clipboard.ImagePng, Is.Null);
            Assert.That(answer.Unchanged, Is.Null);
            Assert.That(answer.Text, Is.EqualTo("copied on C"));
        }
    }

    // -- 4. a copy on A after its own answer started streaming to the master --

    [TestCase(true)]
    [TestCase(false)]
    public async Task UserCopiesOnAWhileItsAnswerStreams_AKeepsTheCopyAndItsNextPullAnswerCarriesIt(bool answerCompletesFirst)
    {
        var clipboard = new FakeClipboardSync();
        var older = BigImage(1);
        clipboard.SetupImage(older);
        var slave = Slave(clipboard);
        slave.Reliable.Hold();
        await Pull(slave);
        await slave.Sent.WaitFor(MessageKind.ClipboardStreamBegin, 1, Master);
        var olderId = slave.Sent.Of<ClipboardStreamBeginMessage>(MessageKind.ClipboardStreamBegin, Master).Single().Id;

        var newer = BigImage(2);
        clipboard.CanUseEchoFallbackValue = false;
        clipboard.SetupImage(newer);
        ClipboardPullResponseMessage? first = null;
        if (answerCompletesFirst)
        {
            slave.Reliable.Release();
            first = await SentAnswer(slave, begins: 1);
            // the master took it, so it pulls against it
            await Pull(slave, 2, ImageHash(older));
        }
        else
        {
            await Pull(slave, 2);
            slave.Reliable.Release();
        }
        var answer = await SentAnswer(slave, begins: 2);

        var sent = slave.Sent.Snapshot();
        var newerId = slave.Sent.Of<ClipboardStreamBeginMessage>(MessageKind.ClipboardStreamBegin, Master).Last().Id;
        using (Assert.EnterMultipleScope())
        {
            Assert.That(clipboard.ImagePng, Is.EqualTo(newer));
            Assert.That(answer.PullId, Is.EqualTo(2));
            Assert.That(answer.ImagePng, Is.EqualTo(newer));
            if (answerCompletesFirst)
            {
                Assert.That(first!.ImagePng, Is.EqualTo(older));
            }
            else
            {
                Assert.That(AbortsTo(slave.Sent, Master), Is.EqualTo([olderId]));
                Assert.That(IndexOf<ClipboardStreamAbortMessage>(sent, MessageKind.ClipboardStreamAbort, Master, a => a.Id == olderId),
                    Is.LessThan(IndexOf<ClipboardStreamBeginMessage>(sent, MessageKind.ClipboardStreamBegin, Master, b => b.Id == newerId)));
                Assert.That(slave.Sent.Of<ClipboardStreamChunkMessage>(MessageKind.ClipboardStreamChunk).Select(c => c.Id), Has.None.EqualTo(olderId));
            }
        }
    }

    [Test]
    public async Task UserCopiesOnAWhileItsAnswerStreams_TheMastersOfferOfThatOlderAnswerLeavesTheCopy()
    {
        var clipboard = new FakeClipboardSync();
        var older = BigImage(1);
        clipboard.SetupImage(older);
        var slave = Slave(clipboard);
        slave.Reliable.Hold();
        await Pull(slave);
        await slave.Sent.WaitFor(MessageKind.ClipboardStreamBegin, 1, Master);

        var newer = BigImage(2);
        clipboard.CanUseEchoFallbackValue = false;
        clipboard.SetupImage(newer);
        slave.Reliable.Release();
        await SentAnswer(slave, begins: 1);
        // the cursor is back on A when the older answer lands, so the master offers it to A, and pulls on leaving
        var landed = ClipboardWire.ToHash(new ClipboardSnapshot(null, null, older));
        await slave.SimulateReceive(Master, MessageKind.ClipboardHash, landed.ToSaneJson());
        await PushIfRequested(slave, new ClipboardPushMessage("", ImagePng: older));
        await Pull(slave, 2, landed.Hash);
        var answer = await SentAnswer(slave, begins: 2);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(clipboard.ImagePng, Is.EqualTo(newer));
            Assert.That(answer.PullId, Is.EqualTo(2));
            Assert.That(answer.ImagePng, Is.EqualTo(newer));
        }
    }

    [TestCase(true)]
    [TestCase(false)]
    public async Task AsOlderAnswerStreamsThenANewerPull_TheMasterEndsWithTheNewerCopyAndNeverPastesTheOlderAfterThatPull(bool streamCompletesBeforeTheNextPull)
    {
        var clipboard = new FakeClipboardSync();
        clipboard.SetupText("old on the master");
        var (platform, relay, service) = await StartMaster(clipboard);
        VisitAndLeaveRemote(platform);
        var older = BigImage(2);
        var stream = StreamedAnswer(1, new ClipboardPullResponseMessage(PullIdTo(relay), null, ImagePng: older));
        await Begin(relay, stream);
        await Chunk(relay, stream.Chunks[0]);
        if (streamCompletesBeforeTheNextPull) await Chunks(relay, stream, from: 1);
        var landed = clipboard.ImagePng;

        // on A and off again, which pulls A's newer copy
        VisitAndLeaveRemote(platform);
        var next = PullIdTo(relay);
        if (!streamCompletesBeforeTheNextPull) await Chunks(relay, stream, from: 1);
        var afterTheNextPull = clipboard.ImagePng;
        await AnswerPull(relay, new ClipboardPullResponseMessage(next, "copied on A since"));
        await service.Flush();
        await service.HashReads();

        using (Assert.EnterMultipleScope())
        {
            if (streamCompletesBeforeTheNextPull) Assert.That(landed, Is.EqualTo(older));
            else Assert.That(afterTheNextPull, Is.Not.EqualTo(older));
            Assert.That(clipboard.Text, Is.EqualTo("copied on A since"));
            Assert.That(clipboard.ImagePng, Is.Null);
        }
    }

    // -- 5. copies on two slaves in quick succession, both answering with streams --

    public enum Completion { NewerFirst, OlderAfterTheNewerBegins, OlderBeforeTheNewerBegins }

    [TestCase(Completion.NewerFirst)]
    [TestCase(Completion.OlderAfterTheNewerBegins)]
    [TestCase(Completion.OlderBeforeTheNewerBegins)]
    public async Task CopiesOnAThenC_BothStream_TheLaterPullsCopyWinsOnTheMasterAndTheOtherStreamIsAborted(Completion completion)
    {
        var clipboard = new FakeClipboardSync();
        clipboard.SetupText("old on the master");
        var (platform, relay, service) = await StartSides(clipboard);
        var olderImage = BigImage(2);
        var older = await StreamFromRemoteThenVisitTheLeftHost(platform, relay, olderImage);
        var newerImage = BigImage(3);
        var newer = StreamedAnswer(7, new ClipboardPullResponseMessage(PullIdTo(relay, "remote2"), null, ImagePng: newerImage));

        byte[]? whileTheNewerStreams = null;
        switch (completion)
        {
            case Completion.NewerFirst:
                await Begin(relay, newer, "remote2");
                await Chunks(relay, newer, host: "remote2");
                await Chunks(relay, older, from: 1);
                break;
            case Completion.OlderAfterTheNewerBegins:
                await Begin(relay, newer, "remote2");
                await Chunks(relay, newer, host: "remote2", to: newer.Chunks.Length - 1);
                await Chunks(relay, older, from: 1);
                whileTheNewerStreams = clipboard.ImagePng;
                await Chunk(relay, newer.Chunks[^1], "remote2");
                break;
            case Completion.OlderBeforeTheNewerBegins:
                await Chunks(relay, older, from: 1);
                await Begin(relay, newer, "remote2");
                await Chunks(relay, newer, host: "remote2");
                break;
        }
        await service.Flush();
        await service.HashReads();

        var aborted = AbortsTo(relay.Sent, "remote");
        using (Assert.EnterMultipleScope())
        {
            Assert.That(clipboard.ImagePng, Is.EqualTo(newerImage));
            // a stream that completed before the newer copy was announced has nothing left to abort
            Assert.That(aborted, completion == Completion.OlderBeforeTheNewerBegins ? Is.Empty : Is.EqualTo([1L]));
            Assert.That(relay.Sent.Of(MessageKind.ClipboardStreamAbort, "remote2"), Is.Empty);
            if (completion == Completion.OlderAfterTheNewerBegins) Assert.That(whileTheNewerStreams, Is.Not.EqualTo(olderImage));
        }
    }

    // -- 6. copy, then copy again on the master, superseding a push to A twice --

    [Test]
    public async Task TwoCopiesOnTheMasterSupersedeAPushToA_OnlyTheNewestArrives_EachAbortGoingBeforeItsReplacement()
    {
        var clipboard = new FakeClipboardSync();
        clipboard.SetupImage(BigImage(1));
        var (platform, relay, _) = await StartMaster(clipboard);
        TransitionTestHelper.EnterRemote(platform);
        relay.Reliable.Hold();
        await SimulatePullRequest(relay);
        await relay.Sent.WaitFor(MessageKind.ClipboardStreamBegin, 1, "remote");
        var first = relay.Sent.Of<ClipboardStreamBeginMessage>(MessageKind.ClipboardStreamBegin, "remote").Single().Id;

        clipboard.CanUseEchoFallbackValue = false;
        clipboard.SetupImage(BigImage(2));
        await SimulatePullRequest(relay);
        var newest = BigImage(3);
        clipboard.SetupImage(newest);
        await SimulatePullRequest(relay);
        relay.Reliable.Release();
        var push = await SentPush(relay, "remote", begins: 2);

        var sent = relay.Sent.Snapshot();
        var newestId = relay.Sent.Of<ClipboardStreamBeginMessage>(MessageKind.ClipboardStreamBegin, "remote").Last().Id;
        var aborts = AbortsTo(relay.Sent, "remote");
        using (Assert.EnterMultipleScope())
        {
            Assert.That(push.ImagePng, Is.EqualTo(newest));
            Assert.That(relay.Sent.Of(MessageKind.ClipboardPush), Is.Empty);
            Assert.That(aborts, Has.Count.EqualTo(2));
            Assert.That(aborts, Is.Unique);
            Assert.That(aborts[0], Is.EqualTo(first));
            Assert.That(aborts, Has.None.EqualTo(newestId));
            Assert.That(relay.Sent.Of<ClipboardStreamChunkMessage>(MessageKind.ClipboardStreamChunk).Select(c => c.Id), Is.All.EqualTo(newestId));
            var firstAbort = IndexOf<ClipboardStreamAbortMessage>(sent, MessageKind.ClipboardStreamAbort, "remote", a => a.Id == aborts[0]);
            var secondAbort = IndexOf<ClipboardStreamAbortMessage>(sent, MessageKind.ClipboardStreamAbort, "remote", a => a.Id == aborts[^1]);
            var newestBegin = IndexOf<ClipboardStreamBeginMessage>(sent, MessageKind.ClipboardStreamBegin, "remote", b => b.Id == newestId);
            Assert.That(firstAbort, Is.LessThan(secondAbort));
            Assert.That(secondAbort, Is.LessThan(newestBegin));
            // the replaced copy's Begin, if it went at all, went after the abort it replaced
            var secondBegin = IndexOf<ClipboardStreamBeginMessage>(sent, MessageKind.ClipboardStreamBegin, "remote", b => b.Id == aborts[^1]);
            if (secondBegin >= 0) Assert.That(secondBegin, Is.GreaterThan(firstAbort).And.LessThan(secondAbort));
        }
    }

    [Test]
    public async Task PushSupersededTwice_StaleChunksOfOlderIdsNeverApply_OnlyTheNewestLands()
    {
        var clipboard = new FakeClipboardSync();
        var osd = new RecordingOsd();
        var clock = new FakeTimeProvider();
        var slave = Slave(clipboard, osd, clock);
        var first = HashedPush(1, new ClipboardPushMessage(BigText('a')));
        var second = HashedPush(2, new ClipboardPushMessage(BigText('b')));
        var newest = HashedPush(3, new ClipboardPushMessage(BigText('c')));

        await Begin(slave, first);
        await Chunk(slave, first.Chunks[0]);
        await Abort(slave, 1);
        await Begin(slave, second);
        // the bulk lane still delivering the older stream's chunks behind the newer Begin
        await Chunk(slave, first.Chunks[1]);
        await Chunk(slave, second.Chunks[0]);
        await Abort(slave, 2);
        await Begin(slave, newest);
        await Chunks(slave, second, from: 1);
        await Chunks(slave, first, from: 2);
        var beforeTheNewest = new { clipboard.Text, clipboard.SetClipboardCallCount };
        await Chunks(slave, newest);
        // a stream still pending would time out here
        clock.Advance(ClipboardStreamReceiver.Watchdog);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(beforeTheNewest.Text, Is.Null);
            Assert.That(beforeTheNewest.SetClipboardCallCount, Is.Zero);
            Assert.That(clipboard.Text, Is.EqualTo(BigText('c')));
            Assert.That(clipboard.SetClipboardCallCount, Is.EqualTo(1));
            Assert.That(osd.Shown.Count, Is.Zero);
        }
    }

    // -- 7. the master's own copy between A's Begin and its last chunk --

    [TestCase(0)]
    [TestCase(1)]
    [TestCase(-1)]
    public async Task MastersOwnCopyMidAnswerFromA_TheMasterKeepsItAbortsAsStreamAndNeverTakesIt(int chunksBeforeTheCopy)
    {
        var clipboard = new FakeClipboardSync();
        clipboard.SetupText("old on the master");
        var (platform, relay, service) = await StartMaster(clipboard);
        VisitAndLeaveRemote(platform);
        var stream = StreamedAnswer(3, new ClipboardPullResponseMessage(PullIdTo(relay), null, ImagePng: BigImage(2)));
        // -1 is every chunk but the last
        var before = chunksBeforeTheCopy < 0 ? stream.Chunks.Length - 1 : chunksBeforeTheCopy;
        await Begin(relay, stream);
        await Chunks(relay, stream, to: before);

        clipboard.CanUseEchoFallbackValue = false;
        clipboard.SetupText("copied on the master");
        var setBefore = clipboard.SetClipboardCallCount;
        await Chunks(relay, stream, from: before);
        await service.Flush();
        await service.HashReads();
        // the copy is what A is offered next
        TransitionTestHelper.EnterRemote(platform);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(AbortsTo(relay.Sent, "remote"), Is.EqualTo([3L]));
            Assert.That(clipboard.Text, Is.EqualTo("copied on the master"));
            Assert.That(clipboard.ImagePng, Is.Null);
            Assert.That(clipboard.SetClipboardCallCount, Is.EqualTo(setBefore));
            Assert.That(Hashes(relay, "remote")[^1].Hash, Is.EqualTo(TextHash("copied on the master")));
        }
    }

    // -- 8. the master's own copy after its pull went out, before A's answer is applied --

    [Test]
    public async Task MastersOwnCopyAfterThePull_AWholeAnswerFromAIsIgnored()
    {
        var clipboard = new FakeClipboardSync();
        clipboard.SetupText("old on the master");
        var (platform, relay, service) = await StartMaster(clipboard);
        VisitAndLeaveRemote(platform);
        var pullId = PullIdTo(relay);

        clipboard.SetupText("copied on the master");
        await AnswerPull(relay, new ClipboardPullResponseMessage(pullId, "copied on A"));
        await service.Flush();
        await service.HashReads();
        TransitionTestHelper.EnterRemote(platform);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(clipboard.Text, Is.EqualTo("copied on the master"));
            Assert.That(clipboard.SetClipboardCallCount, Is.Zero);
            Assert.That(Hashes(relay, "remote")[^1].Hash, Is.EqualTo(TextHash("copied on the master")));
        }
    }

    [Test]
    public async Task MastersOwnCopyBeforeAsLastChunk_TheStreamedAnswerIsNotApplied()
    {
        var clipboard = new FakeClipboardSync();
        clipboard.SetupText("old on the master");
        var (platform, relay, service) = await StartMaster(clipboard);
        VisitAndLeaveRemote(platform);
        var stream = StreamedAnswer(3, new ClipboardPullResponseMessage(PullIdTo(relay), null, ImagePng: BigImage(2)));
        await Begin(relay, stream);
        await Chunks(relay, stream, to: stream.Chunks.Length - 1);

        // a copy the per-chunk check cannot see yet: the clipboard still reads as Hydra's
        clipboard.SetupText("copied on the master");
        await Chunks(relay, stream, from: stream.Chunks.Length - 1);
        await service.Flush();
        await service.HashReads();
        TransitionTestHelper.EnterRemote(platform);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(clipboard.Text, Is.EqualTo("copied on the master"));
            Assert.That(clipboard.ImagePng, Is.Null);
            Assert.That(clipboard.SetClipboardCallCount, Is.Zero);
            Assert.That(Hashes(relay, "remote")[^1].Hash, Is.EqualTo(TextHash("copied on the master")));
        }
    }

    // -- helpers: a slave --

    // the master could not write A's copy (Windows' clipboard busy): A, synced with that copy as it answered, would answer
    // every later pull Unchanged; the master's next pull asks a resend, so the copy lands then
    [Test]
    public async Task MasterCouldNotWriteAsCopy_ItLandsOnTheNextPull()
    {
        var clipboard = new FakeClipboardSync();
        clipboard.SetupText("old on the master");
        var bundle = await StartMaster(clipboard);
        var onA = new FakeClipboardSync();
        onA.SetupText("copied on A");
        onA.CanUseEchoFallbackValue = false;
        var a = Slave(onA);

        clipboard.WriteFailsValue = true;
        await LeaveAndAnswer(bundle, a, 1);
        var afterFailure = clipboard.Text;
        clipboard.WriteFailsValue = false;
        await LeaveAndAnswer(bundle, a, 2);

        var pulls = bundle.Relay.Sent.Of<ClipboardPullMessage>(MessageKind.ClipboardPull, "remote");
        using (Assert.EnterMultipleScope())
        {
            Assert.That(afterFailure, Is.EqualTo("old on the master"), "pre-condition: the write failed");
            Assert.That(pulls.Select(p => p.ResendFrom), Is.EqualTo(new int?[] { null, pulls[0].PullId }));
            Assert.That(clipboard.Text, Is.EqualTo("copied on A"));
        }
    }

    // as above, the cursor going back onto A before the next pull: the entry hash, the master's older clipboard, says the
    // master never took A's copy, so A keeps it rather than empty it for the older push, and the pull on leaving lands it
    [Test]
    public async Task MasterCouldNotWriteAsCopy_ReenteringAKeepsIt_AndItLandsOnLeaving()
    {
        var clipboard = new FakeClipboardSync();
        clipboard.SetupText("old on the master");
        var bundle = await StartMaster(clipboard);
        var onA = new FakeClipboardSync();
        onA.SetupText("copied on A");
        onA.CanUseEchoFallbackValue = false;
        var a = Slave(onA);
        clipboard.WriteFailsValue = true;
        await LeaveAndAnswer(bundle, a, 1);
        clipboard.WriteFailsValue = false;

        TransitionTestHelper.EnterRemote(bundle.Platform);
        await bundle.Service.HashReads();
        var hash = bundle.Relay.Sent.Of(MessageKind.ClipboardHash, "remote").Last();
        await a.SimulateReceive(Master, hash.Kind, hash.Json);
        await a.ClipboardIdle();
        var onAAfterTheHash = onA.Text;
        LeaveRemote(bundle.Platform);
        await AnswerTheLatestPull(bundle, a, 2);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(onAAfterTheHash, Is.EqualTo("copied on A"), "kept, never emptied for the older push");
            Assert.That(onA.ClearClipboardCallCount, Is.Zero);
            Assert.That(clipboard.Text, Is.EqualTo("copied on A"));
        }
    }

    // A answered the pull in full with its copy, still on its way, when the user copies on the master and goes back onto A:
    // that copy is newer than A's answer, so A takes it (asks the push), never keeps its older one, and the answer landing
    // late is ignored
    [Test]
    public async Task CopyOnTheMasterWhileAsAnswerIsOnItsWay_ReenteringATakesTheCopy()
    {
        var clipboard = new FakeClipboardSync();
        clipboard.SetupText("old on the master");
        var bundle = await StartMaster(clipboard);
        var onA = new FakeClipboardSync();
        onA.SetupText("copied on A");
        onA.CanUseEchoFallbackValue = false;
        var a = Slave(onA);
        VisitAndLeaveRemote(bundle.Platform);
        var pull = bundle.Relay.Sent.Of(MessageKind.ClipboardPull, "remote").Last();
        await a.SimulateReceive(Master, pull.Kind, pull.Json);
        await a.Sent.WaitFor(MessageKind.ClipboardPullResponse, 1, Master);

        clipboard.CanUseEchoFallbackValue = false;
        clipboard.SetupText("z on the master");
        TransitionTestHelper.EnterRemote(bundle.Platform);
        await bundle.Service.HashReads();
        var hash = bundle.Relay.Sent.Of(MessageKind.ClipboardHash, "remote").Last();
        await a.SimulateReceive(Master, hash.Kind, hash.Json);
        await a.ClipboardIdle();
        var answer = a.Sent.Of(MessageKind.ClipboardPullResponse, Master).Single();
        await bundle.Relay.FireMessageReceived("remote", answer.Kind, answer.Json);
        await bundle.Service.Flush();

        using (Assert.EnterMultipleScope())
        {
            Assert.That(Hashes(bundle.Relay, "remote")[^1].ResendFrom, Is.Null);
            Assert.That(a.Sent.Of(MessageKind.ClipboardPullRequest, Master), Has.Count.EqualTo(1), "A asks for the master's copy");
            Assert.That(clipboard.Text, Is.EqualTo("z on the master"), "A's late answer is ignored");
        }
    }

    // onto the remote and back, its pull answered by slave, the answer'th it sent, taken by the master
    private static async Task LeaveAndAnswer(TestServiceBundle bundle, TestableSlaveRelay slave, int answer)
    {
        VisitAndLeaveRemote(bundle.Platform);
        await AnswerTheLatestPull(bundle, slave, answer);
    }

    // the latest pull to the remote answered by slave, the answer'th it sent, taken by the master
    private static async Task AnswerTheLatestPull(TestServiceBundle bundle, TestableSlaveRelay slave, int answer)
    {
        var relay = bundle.Relay;
        var pull = relay.Sent.Of(MessageKind.ClipboardPull, "remote").Last();
        await slave.SimulateReceive(Master, pull.Kind, pull.Json);
        await slave.Sent.WaitFor(MessageKind.ClipboardPullResponse, answer, Master);
        var response = slave.Sent.Of(MessageKind.ClipboardPullResponse, Master).Last();
        await relay.FireMessageReceived("remote", response.Kind, response.Json);
        await bundle.Service.Flush();
    }

    private TestableSlaveRelay Slave(FakeClipboardSync clipboard, RecordingOsd? osd = null, FakeTimeProvider? clock = null) =>
        _slaves.New(clipboard, osd ?? new RecordingOsd(), clock ?? new FakeTimeProvider());

    // the master's push, if the slave asked for one
    private static async Task PushIfRequested(TestableSlaveRelay slave, ClipboardPushMessage push)
    {
        if (slave.Sent.Of(MessageKind.ClipboardPullRequest, Master).Count > 0)
            await slave.SimulateReceive(Master, MessageKind.ClipboardPush, push.ToSaneJson());
    }
}

using System.Text.Json;
using Cathedral.Config;
using Hydra.Config;
using Hydra.Relay;
using Hydra.Screen;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Time.Testing;
using Tests.Setup;
using static Tests.Setup.ClipboardStreaming;

namespace Tests.Relay.Edge;

// copying again while an older copy still streams to the master: a pull answer that begins streaming is a changed
// clipboard, so it supersedes every older pull from its Begin, and the master never holds the stale older copy after it
[TestFixture]
public class ClipboardEdgeRecopyTests : MasterClipboardFixture
{
    private readonly SlaveRelays _slaves = new();

    [TearDown]
    public Task StopSlaves() => _slaves.StopAll();

    // -- 1. X1 streaming from A, back onto A, X2 copied there, and back to the master --

    public enum OldAnswer { LateChunksAfterTheNewBegin, CompletedBeforeTheNewBegin }

    [TestCase(OldAnswer.LateChunksAfterTheNewBegin)]
    [TestCase(OldAnswer.CompletedBeforeTheNewBegin)]
    public async Task RecopyOnAWhileItsOlderCopyStreams_TheMasterEndsWithTheNewerAndNeverHoldsTheOlderAfterThePull(OldAnswer old)
    {
        var clipboard = new FakeClipboardSync();
        clipboard.SetupText("old on the master");
        var (platform, relay, service) = await StartTri(clipboard);
        VisitAndLeaveRemote(platform);
        var x1 = BigImage(1);
        var older = StreamedAnswer(1, new ClipboardPullResponseMessage(PullIdTo(relay, "remote"), null, ImagePng: x1));
        await Begin(relay, older);
        await Chunk(relay, older.Chunks[0]);

        // back onto A, where the user copies X2, and off it again: pull #2
        VisitAndLeaveRemote(platform);
        Assert.That(relay.Sent.Of(MessageKind.ClipboardPull, "remote"), Has.Count.EqualTo(2), "pre-condition: pulled from A again");
        var x2 = BigImage(2, 2);
        var newer = StreamedAnswer(2, new ClipboardPullResponseMessage(PullIdTo(relay, "remote"), null, ImagePng: x2));
        var held = new List<byte[]?>();

        if (old == OldAnswer.CompletedBeforeTheNewBegin)
        {
            await Chunks(relay, older, from: 1, track: () => held.Add(clipboard.ImagePng));
            await Begin(relay, newer);
        }
        else
        {
            // A aborts its older stream before beginning the newer one
            await Abort(relay, 1);
            await Begin(relay, newer);
            await Chunks(relay, older, from: 1, track: () => held.Add(clipboard.ImagePng));
        }
        var setBeforeTheNewer = clipboard.SetClipboardCallCount;
        await Chunks(relay, newer, track: () => held.Add(clipboard.ImagePng));
        await service.Flush();
        await service.HashReads();

        using (Assert.EnterMultipleScope())
        {
            Assert.That(clipboard.ImagePng, Is.EqualTo(x2));
            Assert.That(held, Has.None.EqualTo(x1));
            Assert.That(setBeforeTheNewer, Is.Zero, "the older copy applied nothing");
            Assert.That(clipboard.SetClipboardCallCount, Is.EqualTo(1));
            Assert.That(relay.Sent.Of(MessageKind.ClipboardStreamAbort, "remote"), Is.Empty, "the master had no other host's stream to abort");
        }
    }

    [Test]
    public async Task RecopyOnAAfterItsOlderCopyLanded_TheMasterHoldsTheOlderUntilTheNewerLands()
    {
        var clipboard = new FakeClipboardSync();
        clipboard.SetupText("old on the master");
        var (platform, relay, service) = await StartTri(clipboard);
        VisitAndLeaveRemote(platform);
        var x1 = BigImage(1);
        var older = StreamedAnswer(1, new ClipboardPullResponseMessage(PullIdTo(relay, "remote"), null, ImagePng: x1));
        await Begin(relay, older);
        await Chunk(relay, older.Chunks[0]);

        // back onto A, where X1 lands before the user copies X2 and leaves again
        TransitionTestHelper.EnterRemote(platform);
        await Chunks(relay, older, from: 1);
        var afterTheOlder = clipboard.ImagePng;
        platform.FireMouseMove(1280, 720);
        platform.FireMouseMove(1275, 720);
        Assert.That(relay.Sent.Of(MessageKind.ClipboardPull, "remote"), Has.Count.EqualTo(2), "pre-condition: pulled from A again");
        var x2 = BigImage(2, 2);
        var newer = StreamedAnswer(2, new ClipboardPullResponseMessage(PullIdTo(relay, "remote"), null, ImagePng: x2));
        await Begin(relay, newer);
        var atTheNewerBegin = clipboard.ImagePng;
        await Chunks(relay, newer);
        await service.Flush();
        await service.HashReads();

        using (Assert.EnterMultipleScope())
        {
            Assert.That(afterTheOlder, Is.EqualTo(x1), "the newest copy then");
            Assert.That(atTheNewerBegin, Is.Null);
            Assert.That(clipboard.ImagePng, Is.EqualTo(x2));
            Assert.That(clipboard.SetClipboardCallCount, Is.EqualTo(2));
        }
    }

    // -- 2. the slave side of 1: A streaming X1 when a new pull finds X2 on its clipboard --

    [Test]
    public async Task SlaveStreamingAnOlderAnswer_ANewPullFindsANewerCopy_AbortsTheOlderThenStreamsTheNewer()
    {
        var clipboard = new FakeClipboardSync();
        var x1 = BigImage(1);
        clipboard.SetupImage(x1);
        var slave = Slave(clipboard);
        slave.Reliable.Hold();
        await Pull(slave);
        await slave.Sent.WaitFor(MessageKind.ClipboardStreamBegin, 1, Master);
        var olderId = slave.Sent.Of<ClipboardStreamBeginMessage>(MessageKind.ClipboardStreamBegin, Master).Single().Id;

        var x2 = BigImage(2, 2);
        clipboard.CanUseEchoFallbackValue = false;
        clipboard.SetupImage(x2);
        await Pull(slave, 2);
        slave.Reliable.Release();
        var answer = await SentAnswer(slave, begins: 2);

        var sent = slave.Sent.Snapshot();
        var newerId = slave.Sent.Of<ClipboardStreamBeginMessage>(MessageKind.ClipboardStreamBegin, Master).Last().Id;
        var abort = IndexOf<ClipboardStreamAbortMessage>(sent, MessageKind.ClipboardStreamAbort, Master, a => a.Id == olderId);
        var begin = IndexOf<ClipboardStreamBeginMessage>(sent, MessageKind.ClipboardStreamBegin, Master, b => b.Id == newerId);
        var lastOlderChunk = LastIndexOf<ClipboardStreamChunkMessage>(sent, MessageKind.ClipboardStreamChunk, Master, c => c.Id == olderId);
        using (Assert.EnterMultipleScope())
        {
            Assert.That(newerId, Is.Not.EqualTo(olderId));
            Assert.That(AbortsTo(slave.Sent, Master), Is.EqualTo([olderId]));
            Assert.That(abort, Is.LessThan(begin));
            Assert.That(lastOlderChunk, Is.EqualTo(-1), "the abort beat every chunk of the older stream: its Begin was still parked");
            Assert.That(answer.PullId, Is.EqualTo(2));
            Assert.That(answer.ImagePng, Is.EqualTo(x2));
            Assert.That(slave.Sent.Of(MessageKind.ClipboardPullResponse, Master), Is.Empty);
        }
    }

    // -- 3. X streaming from A when B's larger Y begins streaming --

    [TestCase(false)]
    [TestCase(true)]
    public async Task NewerCopyBeginsStreamingFromB_TheOlderFromAIsAbortedAtThatBeginAndNeverApplied(bool newerFirst)
    {
        var clipboard = new FakeClipboardSync();
        clipboard.SetupText("old on the master");
        var (platform, relay, service) = await StartTri(clipboard);
        var x = BigImage(1);
        var older = await StreamFromAThenCopyOnB(platform, relay, x);
        var y = BigImage(2, 2);
        var newer = StreamedAnswer(7, new ClipboardPullResponseMessage(PullIdTo(relay, "remote2"), null, ImagePng: y));

        await Begin(relay, newer, "remote2");
        var abortedAtBegin = AbortsTo(relay.Sent, "remote");
        var held = new List<byte[]?> { clipboard.ImagePng };
        void Track() => held.Add(clipboard.ImagePng);
        if (newerFirst)
        {
            await Chunks(relay, newer, host: "remote2", track: Track);
            await Chunks(relay, older, from: 1, track: Track);
        }
        else
        {
            await Chunks(relay, older, from: 1, track: Track);
            await Chunks(relay, newer, host: "remote2", track: Track);
        }
        await service.Flush();
        await service.HashReads();

        using (Assert.EnterMultipleScope())
        {
            Assert.That(abortedAtBegin, Is.EqualTo([1L]));
            Assert.That(AbortsTo(relay.Sent, "remote"), Is.EqualTo([1L]));
            Assert.That(relay.Sent.Of(MessageKind.ClipboardStreamAbort, "remote2"), Is.Empty);
            Assert.That(held, Has.None.EqualTo(x));
            Assert.That(clipboard.ImagePng, Is.EqualTo(y));
            Assert.That(clipboard.SetClipboardCallCount, Is.EqualTo(1), "only Y was applied");
        }
    }

    // -- 4. X streaming from A when B answers a small copy whole --

    [Test]
    public async Task SmallCopyOnBWhileAStreams_ItsWholeAnswerWinsAndAIsAborted()
    {
        var clipboard = new FakeClipboardSync();
        clipboard.SetupText("old on the master");
        var (platform, relay, service) = await StartTri(clipboard);
        var x = BigImage(1);
        var older = await StreamFromAThenCopyOnB(platform, relay, x);

        await AnswerPull(relay, new ClipboardPullResponseMessage(PullIdTo(relay, "remote2"), "Hello world!"), "remote2");
        var setAfterTheAnswer = clipboard.SetClipboardCallCount;
        var held = new List<byte[]?>();
        await Chunks(relay, older, from: 1, track: () => held.Add(clipboard.ImagePng));
        await service.Flush();
        await service.HashReads();

        using (Assert.EnterMultipleScope())
        {
            Assert.That(AbortsTo(relay.Sent, "remote"), Is.EqualTo([1L]));
            Assert.That(held, Has.None.EqualTo(x));
            Assert.That(clipboard.Text, Is.EqualTo("Hello world!"));
            Assert.That(clipboard.ImagePng, Is.Null);
            Assert.That(clipboard.SetClipboardCallCount, Is.EqualTo(setAfterTheAnswer));
        }
    }

    // -- 5. as 3, but Y's stream fails after superseding X --

    [Test]
    public async Task NewerStreamFailsAfterSupersedingTheOlder_TheMasterEndsEmptyAndTheOlderDoesNotResurrect()
    {
        var clipboard = new FakeClipboardSync();
        clipboard.SetupText("old on the master");
        var osd = new RecordingOsd();
        var log = new CapturingLog<InputRouter>();
        var (platform, relay, service) = await StartTri(clipboard, osd, log);
        var x = BigImage(1);
        var older = await StreamFromAThenCopyOnB(platform, relay, x);
        var newer = StreamedAnswer(7, new ClipboardPullResponseMessage(PullIdTo(relay, "remote2"), null, ImagePng: BigImage(2, 2)), blobHash: 12345);

        await Begin(relay, newer, "remote2");
        var held = new List<byte[]?>();
        void Track() => held.Add(clipboard.ImagePng);
        await Chunks(relay, older, from: 1, track: Track);
        await Chunks(relay, newer, host: "remote2", track: Track);
        await service.Flush();
        await service.HashReads();

        using (Assert.EnterMultipleScope())
        {
            Assert.That(await osd.Shown.Next("the failure"), Is.EqualTo(ClipboardStreamReceiver.FailedOsd));
            Assert.That(log.Entries.Where(e => e.Level == LogLevel.Warning).Select(e => e.Message), Has.Some.Contains("remote2").And.Contains("match"));
            Assert.That(AbortsTo(relay.Sent, "remote"), Is.EqualTo([1L]));
            Assert.That(held, Has.None.EqualTo(x));
            Assert.That(clipboard.Text, Is.Null);
            Assert.That(clipboard.ImagePng, Is.Null);
            Assert.That(clipboard.SetClipboardCallCount, Is.Zero);
        }
    }

    // -- 6. on to C afterwards: it is offered the final winner's hash once --

    [TestCase(true)]
    [TestCase(false)]
    public async Task OnToCAfterTheNewerBegins_CIsOfferedTheWinnersHashOnce(bool whileItStreams)
    {
        var clipboard = new FakeClipboardSync();
        clipboard.SetupText("old on the master");
        var (platform, relay, service) = await StartTri(clipboard);
        var x = BigImage(1);
        var older = await StreamFromAThenCopyOnB(platform, relay, x);
        var y = BigImage(2, 2);
        var newer = StreamedAnswer(7, new ClipboardPullResponseMessage(PullIdTo(relay, "remote2"), null, ImagePng: y));
        await Begin(relay, newer, "remote2");
        await Chunk(relay, newer.Chunks[0], "remote2");

        if (whileItStreams) EnterTopHost(platform, relay);
        await Chunks(relay, older, from: 1);
        await Chunks(relay, newer, from: 1, host: "remote2");
        if (!whileItStreams) EnterTopHost(platform, relay);
        await service.Flush();
        await service.HashReads();

        var hashes = Hashes(relay, "remote3");
        using (Assert.EnterMultipleScope())
        {
            Assert.That(hashes.Select(h => h.Hash), Has.None.EqualTo(ImageHash(x)));
            Assert.That(hashes.Select(h => h.Hash), Has.None.EqualTo(TextHash("old on the master")));
            Assert.That(hashes.Select(h => h.Pending), Is.EqualTo(whileItStreams ? new bool?[] { true, null } : [null]));
            Assert.That(hashes.Select(h => h.Hash), Is.EqualTo(whileItStreams ? new ulong?[] { null, ImageHash(y) } : [ImageHash(y)]));
            Assert.That(clipboard.ImagePng, Is.EqualTo(y));
        }
    }

    [TestCase(true)]
    [TestCase(false)]
    public async Task OnToCAfterBsWholeAnswer_CIsOfferedThatCopysHashOnce(bool beforeTheLateChunks)
    {
        var clipboard = new FakeClipboardSync();
        clipboard.SetupText("old on the master");
        var (platform, relay, service) = await StartTri(clipboard);
        var x = BigImage(1);
        var older = await StreamFromAThenCopyOnB(platform, relay, x);
        await AnswerPull(relay, new ClipboardPullResponseMessage(PullIdTo(relay, "remote2"), "Hello world!"), "remote2");

        if (beforeTheLateChunks) EnterTopHost(platform, relay);
        await Chunks(relay, older, from: 1);
        if (!beforeTheLateChunks) EnterTopHost(platform, relay);
        await service.Flush();
        await service.HashReads();

        using (Assert.EnterMultipleScope())
        {
            Assert.That(Hashes(relay, "remote3").Select(h => h.Pending), Is.EqualTo(new bool?[] { null }));
            Assert.That(Hashes(relay, "remote3").Select(h => h.Hash), Is.EqualTo(new ulong?[] { TextHash("Hello world!") }));
            Assert.That(clipboard.Text, Is.EqualTo("Hello world!"));
        }
    }

    // -- 7. a copy on the master after the pull, before A's streamed answer begins --

    [Test]
    public async Task CopyOnTheMasterAfterThePull_AsStreamedAnswerIsAbortedAtItsBeginAndTheCopyKept()
    {
        var clipboard = new FakeClipboardSync();
        clipboard.SetupText("old on the master");
        var (platform, relay, service) = await StartTri(clipboard);
        VisitAndLeaveRemote(platform);
        var x1 = BigImage(1);
        var older = StreamedAnswer(1, new ClipboardPullResponseMessage(PullIdTo(relay, "remote"), null, ImagePng: x1));

        clipboard.SetupText("copied on the master");
        await Begin(relay, older);
        var held = new List<byte[]?>();
        await Chunks(relay, older, track: () => held.Add(clipboard.ImagePng));
        await service.Flush();
        await service.HashReads();

        using (Assert.EnterMultipleScope())
        {
            Assert.That(AbortsTo(relay.Sent, "remote"), Is.EqualTo([1L]));
            Assert.That(clipboard.ClearClipboardCallCount, Is.Zero, "the copy was never emptied");
            Assert.That(held, Has.None.EqualTo(x1));
            Assert.That(clipboard.Text, Is.EqualTo("copied on the master"));
            Assert.That(clipboard.ImagePng, Is.Null);
            Assert.That(clipboard.SetClipboardCallCount, Is.Zero);
        }
    }

    // -- helpers: the master --

    private static HostConfig Leaf(string name, Direction toHome) =>
        new() { Name = name, Neighbours = [new NeighbourConfig { Direction = toHome, Name = "home" }] };

    // remote2 (B) ← home → remote (A), remote3 (C) above home, all online
    private static readonly IHydraProfile TriConfig = TransitionTestHelper.Profile("home", new HydraConfig
    {
        Mode = Mode.Master,
        Hosts =
        [
            new HostConfig
            {
                Name = "home",
                Neighbours =
                [
                    new NeighbourConfig { Direction = Direction.Left, Name = "remote2" },
                    new NeighbourConfig { Direction = Direction.Right, Name = "remote" },
                    new NeighbourConfig { Direction = Direction.Up, Name = "remote3" },
                ],
            },
            Leaf("remote", Direction.Left),
            Leaf("remote2", Direction.Right),
            Leaf("remote3", Direction.Down),
        ],
    });

    // a fake clock, so no deferred offer's wait runs out on its own
    private Task<TestServiceBundle> StartTri(FakeClipboardSync clipboard, RecordingOsd? osd = null, CapturingLog<InputRouter>? log = null) =>
        Services.StartOnline(
            TransitionTestHelper.CreateService(profile: TriConfig, clipboard: clipboard, osd: osd, log: log, timeProvider: new FakeTimeProvider()),
            "remote", "remote2", "remote3");

    // A's answer streaming in (Begin and a chunk, stream 1) when the cursor goes onto B, where the user copies, and
    // back onto the master's screen, which pulls from B: A's stream
    private static async Task<StreamedClipboard> StreamFromAThenCopyOnB(FakePlatform platform, FakeRelay relay, byte[] image)
    {
        VisitAndLeaveRemote(platform);
        var stream = StreamedAnswer(1, new ClipboardPullResponseMessage(PullIdTo(relay, "remote"), null, ImagePng: image));
        await Begin(relay, stream);
        await Chunk(relay, stream.Chunks[0]);
        platform.FireMouseMove(0, 720);
        Assert.That(relay.Sent.Of(MessageKind.EnterScreen, "remote2"), Has.Count.EqualTo(1), "pre-condition: on B");
        platform.FireMouseDelta(100, 0);
        using (Assert.EnterMultipleScope())
        {
            Assert.That(platform.IsOnVirtualScreen, Is.False, "pre-condition: back on the master's screen");
            Assert.That(relay.Sent.Of(MessageKind.ClipboardPull, "remote2"), Has.Count.EqualTo(1), "pre-condition: pulled from B");
        }
        return stream;
    }

    // from the master's screen across its top edge onto remote3 (C)
    private static void EnterTopHost(FakePlatform platform, FakeRelay relay)
    {
        platform.FireMouseMove(1280, 0);
        Assert.That(relay.Sent.Of(MessageKind.EnterScreen, "remote3"), Has.Count.EqualTo(1), "pre-condition: on C");
    }

    // where in sent the last message of kind to host matching match is, or -1
    private static int LastIndexOf<T>(List<SentMessage> sent, MessageKind kind, string host, Func<T, bool> match) =>
        sent.FindLastIndex(s => s.Kind == kind && s.Targets.Contains(host) && match(JsonSerializer.Deserialize<T>(s.Json, SaneJson.Options)!));

    // -- helpers: a slave --

    private TestableSlaveRelay Slave(FakeClipboardSync clipboard) => _slaves.New(clipboard, new RecordingOsd(), new FakeTimeProvider());
}

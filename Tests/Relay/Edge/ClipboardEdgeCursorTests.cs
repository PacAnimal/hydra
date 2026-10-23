using System.Text.Json;
using Cathedral.Config;
using Hydra.Config;
using Hydra.Relay;
using Microsoft.Extensions.Time.Testing;
using Tests.Setup;
using static Tests.Setup.ClipboardStreaming;

namespace Tests.Relay.Edge;

// cursor storms while a clipboard streams: crossings back and forth never offer a host a stale clipboard, never
// offer one the cursor is not on, never restart or double a stream, and leave nothing pending once the stream lands
[TestFixture]
public class ClipboardEdgeCursorTests : MasterClipboardFixture
{
    private readonly SlaveRelays _slaves = new();

    [TearDown]
    public Task StopSlaves() => _slaves.StopAll();

    // -- 1. A ↔ B ping-pong while A's answer streams to the master --

    [Test]
    public async Task PingPongBetweenTwoSlavesWhileAsAnswerStreams_EachIsOfferedOnlyTheNewestWhileOnItAndNothingPendsAfter()
    {
        var clipboard = new FakeClipboardSync();
        clipboard.SetupText("old on the master");
        var (platform, relay, service) = await Start(TransitionTestHelper.ChainConfig, clipboard, "remote", "remote2");
        var x = BigImage(1);
        var a = new StreamingSlave(relay, "remote", x);
        TransitionTestHelper.EnterRemote(platform);

        // five crossings, A → B → A → B → A → B, a chunk of A's answer after each
        for (var crossing = 0; crossing < 5; crossing++)
        {
            if (crossing % 2 == 0)
            {
                Cross(platform, relay, "remote2", 100);
                await a.Answer(LatestPullTo(relay, "remote"));
            }
            else
            {
                Cross(platform, relay, "remote", -100);
                await AnswerUnchanged(relay, "remote2");
            }
            await a.NextChunk();
            await service.HashReads();
        }
        await a.Finish();
        await service.Flush();
        await service.HashReads();
        var landed = clipboard.ImagePng;
        var toBAtTheEnd = Hashes(relay, "remote2");

        // afterwards: onto A, offered once B answers; home, and back onto A, offered at once
        Cross(platform, relay, "remote", -100);
        await AnswerUnchanged(relay, "remote2");
        await service.HashReads();
        var toAOnTheWayBack = Hashes(relay, "remote").Count;
        ReturnHomeFrom(platform);
        await a.Answer(LatestPullTo(relay, "remote"));
        await service.HashReads();
        var beforeTheEntry = Hashes(relay, "remote").Count;
        TransitionTestHelper.EnterRemote(platform);
        await service.HashReads();
        var atTheEntry = Hashes(relay, "remote");
        await service.Flush();

        using (Assert.EnterMultipleScope())
        {
            Assert.That(landed, Is.EqualTo(x));
            Assert.That(a.Begins, Is.EqualTo(1), "re-crossing A never restarts its answer");
            Assert.That(OffTheCursor(relay), Is.Empty, "a hash only to the host the cursor is on");
            Assert.That(Hashes(relay, "remote2").Select(h => h.Hash), Has.None.EqualTo(TextHash("old on the master")));
            Assert.That(Hashes(relay, "remote").Skip(1).Select(h => h.Hash), Has.None.EqualTo(TextHash("old on the master")));
            Assert.That(Hashes(relay, "remote2").Where(h => h.Pending != true).Select(h => h.Hash), Is.All.EqualTo(ImageHash(x)));
            Assert.That(toBAtTheEnd[^1].Hash, Is.EqualTo(ImageHash(x)), "B, the cursor's host, offered the copy once it landed");
            Assert.That(toBAtTheEnd[^1].Pending, Is.Null);
            Assert.That(toAOnTheWayBack, Is.GreaterThan(1), "A offered once B answered");
            Assert.That(Hashes(relay, "remote")[toAOnTheWayBack - 1].Hash, Is.EqualTo(ImageHash(x)));
            Assert.That(atTheEntry, Has.Count.EqualTo(beforeTheEntry + 1), "nothing pends: the entry hash goes at once");
            Assert.That(atTheEntry[^1].Hash, Is.EqualTo(ImageHash(x)));
            Assert.That(atTheEntry[^1].Pending, Is.Null);
        }
    }

    // -- 2. ping-pong while the master streams pushes to both A and B --

    [Test]
    public async Task PingPongWhilePushesStreamToBothSlaves_TheCursorsHostGoesFirstAndBothCompleteOneStreamPerHost()
    {
        var clipboard = new FakeClipboardSync();
        var m = BigImage(1);
        clipboard.SetupImage(m);
        var (platform, relay, service) = await Start(TransitionTestHelper.ChainConfig, clipboard, "remote", "remote2");
        TransitionTestHelper.EnterRemote(platform);
        relay.Reliable.Hold();
        await SimulatePullRequest(relay);
        await relay.Sent.WaitFor(MessageKind.ClipboardStreamBegin, 1, "remote");

        // onto B, which A's pending push answers unchanged: B is offered the clipboard and asks for it too
        Cross(platform, relay, "remote2", 100);
        await AnswerUnchanged(relay, "remote");
        await service.HashReads();
        Assert.That(Hashes(relay, "remote2").Select(h => h.Hash), Is.EqualTo(new ulong?[] { ImageHash(m) }), "pre-condition: B offered");
        await SimulatePullRequest(relay, "remote2");

        // four more crossings, ending on B, each pull answered unchanged (nothing copied on either since); A has its
        // push's Begin, so the hash matches there, while B, whose Begin is still queued, asks for the push again
        for (var crossing = 0; crossing < 4; crossing++)
        {
            var onA = crossing % 2 == 0;
            var to = onA ? "remote" : "remote2";
            var from = onA ? "remote2" : "remote";
            Cross(platform, relay, to, onA ? -100 : 100);
            await AnswerUnchanged(relay, from);
            await service.HashReads();
            if (to == "remote2") await SimulatePullRequest(relay, "remote2");
        }
        var replaced = AbortsTo(relay.Sent, "remote2");
        relay.Reliable.Release();
        var toA = await SentPush(relay, "remote");
        var toB = await SentPush(relay, "remote2");

        var order = relay.Sent.Snapshot().Where(s => s.Kind is MessageKind.ClipboardStreamBegin or MessageKind.ClipboardStreamChunk)
            .Select(s => s.Targets.Single()).ToList();
        var chunks = relay.Sent.Of(MessageKind.ClipboardStreamChunk, "remote").Count;
        var toBBegins = relay.Sent.Of<ClipboardStreamBeginMessage>(MessageKind.ClipboardStreamBegin, "remote2").Select(b => b.Id).ToList();
        using (Assert.EnterMultipleScope())
        {
            Assert.That(toA.ImagePng, Is.EqualTo(m));
            Assert.That(toB.ImagePng, Is.EqualTo(m));
            // A's Begin, held, then B (the cursor's host) start to end, then A's chunks
            List<string> expected = ["remote", .. Enumerable.Repeat("remote2", 1 + chunks), .. Enumerable.Repeat("remote", chunks)];
            Assert.That(order, Is.EqualTo(expected));
            Assert.That(Overlapping(relay, "remote"), Is.Empty, "never two streams at once to A");
            Assert.That(Overlapping(relay, "remote2"), Is.Empty, "never two streams at once to B");
            Assert.That(relay.Sent.Of(MessageKind.ClipboardStreamBegin, "remote"), Has.Count.EqualTo(1));
            Assert.That(replaced, Is.Not.Empty, "pre-condition: B's queued push was replaced");
            Assert.That(AbortsTo(relay.Sent, "remote2"), Is.EqualTo(replaced), "each replaced push to B aborted as it was replaced");
            Assert.That(toBBegins, Has.Count.EqualTo(1));
            Assert.That(replaced, Has.None.EqualTo(toBBegins.Single()));
            Assert.That(relay.Sent.Of<ClipboardStreamChunkMessage>(MessageKind.ClipboardStreamChunk, "remote2").Select(c => c.Id),
                Has.All.EqualTo(toBBegins.Single()));
            Assert.That(AbortsTo(relay.Sent, "remote"), Is.Empty);
            Assert.That(OffTheCursor(relay), Is.Empty, "a hash only to the host the cursor is on");
        }
    }

    // -- 3. back onto the master while its push to A streams --

    [Test]
    public async Task BackToTheMasterMidPush_TheSlaveAnswersUnchangedTheMastersClipboardStaysAndThePushCompletes()
    {
        var clipboard = new FakeClipboardSync();
        var m = BigImage(1);
        clipboard.SetupImage(m);
        var (platform, relay, service) = await Start(TransitionTestHelper.TestConfig, clipboard, "remote");
        var onA = new FakeClipboardSync();
        onA.SetupText("old on A");
        var a = Slave(onA);
        TransitionTestHelper.EnterRemote(platform);
        relay.Reliable.Hold();
        await SimulatePullRequest(relay);
        await relay.Sent.WaitFor(MessageKind.ClipboardStreamBegin, 1, "remote");
        await Forward(a, relay.Sent.Of(MessageKind.ClipboardStreamBegin, "remote").Single());

        // back onto the master's screen: its pull goes to A, whose pending stream counts as applied
        ReturnHomeFrom(platform);
        await Forward(a, relay.Sent.Of(MessageKind.ClipboardPull, "remote").Single());
        await a.Sent.WaitFor(MessageKind.ClipboardPullResponse, 1, Master);
        var answer = a.Sent.Of(MessageKind.ClipboardPullResponse, Master).Single();
        await relay.FireMessageReceived("remote", answer.Kind, answer.Json);
        await service.Flush();
        var held = new { clipboard.ImagePng, clipboard.SetClipboardCallCount, clipboard.ClearClipboardCallCount };
        relay.Reliable.Release();
        var push = await SentPush(relay, "remote");
        foreach (var chunk in relay.Sent.Of(MessageKind.ClipboardStreamChunk, "remote")) await Forward(a, chunk);

        var unchanged = JsonSerializer.Deserialize<ClipboardPullResponseMessage>(answer.Json, SaneJson.Options)!;
        using (Assert.EnterMultipleScope())
        {
            Assert.That(unchanged.Unchanged, Is.True);
            Assert.That(unchanged.ImagePng, Is.Null);
            Assert.That(a.Sent.Of(MessageKind.ClipboardStreamBegin, Master), Is.Empty, "A streams nothing back");
            Assert.That(held.ImagePng, Is.EqualTo(m));
            Assert.That(held.SetClipboardCallCount, Is.Zero);
            Assert.That(held.ClearClipboardCallCount, Is.Zero);
            Assert.That(clipboard.ImagePng, Is.EqualTo(m));
            Assert.That(push.ImagePng, Is.EqualTo(m));
            Assert.That(AbortsTo(relay.Sent, "remote"), Is.Empty);
            Assert.That(relay.Sent.Of(MessageKind.ClipboardStreamBegin, "remote"), Has.Count.EqualTo(1));
            Assert.That(onA.ImagePng, Is.EqualTo(m), "A's stream completed");
            Assert.That(onA.Text, Is.Null);
        }
    }

    // -- 4. A → master → C → master → C → master while A's answer streams --

    [TestCase(false)]
    [TestCase(true)]
    public async Task InAndOutOfCWhileAsAnswerStreams_TheMasterEndsWithItAndCIsOfferedItOnItsNextEntry(bool landsWhileOnC)
    {
        var clipboard = new FakeClipboardSync();
        clipboard.SetupText("old on the master");
        var (platform, relay, service) = await Start(SidesConfig, clipboard, "remote", "remote2");
        var x = BigImage(1);
        VisitAndLeaveRemote(platform);
        var stream = StreamedAnswer(1, new ClipboardPullResponseMessage(PullIdTo(relay, "remote"), null, ImagePng: x));
        await Begin(relay, stream);
        await Chunk(relay, stream.Chunks[0]);

        EnterLeftHostFromTheMiddle(platform, relay);
        await Chunk(relay, stream.Chunks[1]);
        LeaveLeftHost(platform);
        await AnswerUnchanged(relay, "remote2");
        await Chunk(relay, stream.Chunks[2]);
        EnterLeftHostFromTheMiddle(platform, relay, 2);
        if (landsWhileOnC) await Chunks(relay, stream, from: 3);
        else await Chunk(relay, stream.Chunks[3]);
        await service.HashReads();
        LeaveLeftHost(platform);
        await AnswerUnchanged(relay, "remote2");
        if (!landsWhileOnC) await Chunks(relay, stream, from: 4);
        await service.Flush();
        await service.HashReads();
        var beforeTheEntry = Hashes(relay, "remote2").Count;

        EnterLeftHostFromTheMiddle(platform, relay, 3);
        await service.HashReads();
        var atTheEntry = Hashes(relay, "remote2");
        await service.Flush();

        using (Assert.EnterMultipleScope())
        {
            Assert.That(clipboard.ImagePng, Is.EqualTo(x));
            Assert.That(clipboard.SetClipboardCallCount, Is.EqualTo(1), "applied once, nothing lost or repeated");
            Assert.That(relay.Sent.Of(MessageKind.ClipboardStreamAbort, "remote"), Is.Empty);
            Assert.That(atTheEntry, Has.Count.EqualTo(beforeTheEntry + 1), "C offered at once on its next entry");
            Assert.That(atTheEntry[^1].Hash, Is.EqualTo(ImageHash(x)));
            Assert.That(atTheEntry[^1].Pending, Is.Null);
            Assert.That(atTheEntry.Select(h => h.Hash), Has.None.EqualTo(TextHash("old on the master")));
            if (landsWhileOnC)
                Assert.That(atTheEntry.Take(beforeTheEntry).Select(h => h.Hash), Has.Some.EqualTo(ImageHash(x)), "offered as it landed, the cursor on C");
            // off the cursor only C, told Pending and owed its hash once its wait ended: the landed copy's, at most once
            Assert.That(OffTheCursor(relay), Has.Count.LessThanOrEqualTo(1));
            Assert.That(OffTheCursor(relay), Has.All.StartsWith("remote2: ").And.All.Contains($"\"hash\":{ImageHash(x)}"));
        }
    }

    // -- 5. between one slave's own monitors mid-stream --

    public enum MidStream { PushToTheSlave, AnswerFromTheSlave }

    [TestCase(MidStream.PushToTheSlave)]
    [TestCase(MidStream.AnswerFromTheSlave)]
    public async Task MovesBetweenOneSlavesMonitorsMidStream_TriggerNoPullHashOrStream(MidStream mid)
    {
        var clipboard = new FakeClipboardSync();
        var image = BigImage(1);
        var (platform, relay, service) = await Services.Start(TransitionTestHelper.CreateService(clipboard: clipboard, timeProvider: new FakeTimeProvider()));
        await relay.FirePeersChanged("remote");
        await TransitionTestHelper.AdvertiseScreens(relay, "remote", 2);
        StreamedClipboard? answer = null;
        if (mid == MidStream.PushToTheSlave)
        {
            clipboard.SetupImage(image);
            TransitionTestHelper.EnterRemote(platform);
            relay.Reliable.Hold();
            await SimulatePullRequest(relay);
            await relay.Sent.WaitFor(MessageKind.ClipboardStreamBegin, 1, "remote");
        }
        else
        {
            clipboard.SetupText("old on the master");
            VisitAndLeaveRemote(platform);
            answer = StreamedAnswer(1, new ClipboardPullResponseMessage(PullIdTo(relay, "remote"), null, ImagePng: image));
            await Begin(relay, answer);
            await Chunk(relay, answer.Chunks[0]);
            TransitionTestHelper.EnterRemote(platform);
        }
        await service.Flush();
        await service.HashReads();
        var before = ClipboardSent(relay);

        // remote:0 → remote:1 → remote:0 → remote:1
        ToMonitor(platform, relay, "remote:1", 100);
        ToMonitor(platform, relay, "remote:0", -100);
        ToMonitor(platform, relay, "remote:1", 100);
        await service.Flush();
        var afterTheMoves = ClipboardSent(relay);
        var left = relay.Sent.Of(MessageKind.LeaveScreen).Count;

        if (answer != null) await Chunks(relay, answer, from: 1);
        relay.Reliable.Release();
        if (mid == MidStream.PushToTheSlave) Assert.That((await SentPush(relay, "remote")).ImagePng, Is.EqualTo(image));
        await service.Flush();
        await service.HashReads();

        using (Assert.EnterMultipleScope())
        {
            Assert.That(afterTheMoves, Is.EqualTo(before), "no pull, hash, push, Begin or Abort from a monitor move");
            Assert.That(left, Is.EqualTo(mid == MidStream.PushToTheSlave ? 0 : 1), "never left the host");
            Assert.That(relay.Sent.Of(MessageKind.ClipboardStreamBegin, "remote"), Has.Count.EqualTo(mid == MidStream.PushToTheSlave ? 1 : 0));
            if (mid == MidStream.AnswerFromTheSlave)
            {
                Assert.That(clipboard.ImagePng, Is.EqualTo(image));
                Assert.That(Hashes(relay, "remote").Skip(before[MessageKind.ClipboardHash]).Select(h => h.Hash), Is.EqualTo(new ulong?[] { ImageHash(image) }),
                    "offered once, as it landed");
            }
        }
    }

    // -- helpers: layouts and the cursor --

    // a fake clock, so no deferred offer's wait runs out on its own
    private Task<TestServiceBundle> Start(IHydraProfile profile, FakeClipboardSync clipboard, params string[] hosts) =>
        Services.StartOnline(TransitionTestHelper.CreateService(profile: profile, clipboard: clipboard, timeProvider: new FakeTimeProvider()), hosts);

    // pushes the cursor by dx until it enters host once more
    private static void Cross(FakePlatform platform, FakeRelay relay, string host, int dx)
    {
        var entries = relay.Sent.Of(MessageKind.EnterScreen, host).Count + 1;
        bool Entered() => relay.Sent.Of(MessageKind.EnterScreen, host).Count >= entries;
        for (var i = 0; i < 40 && !Entered(); i++)
            platform.FireMouseDelta(dx, 0);
        Assert.That(Entered(), Is.True, $"pre-condition: on {host}");
    }

    // pushes the cursor by dx until it enters screen once more, on the host it is on
    private static void ToMonitor(FakePlatform platform, FakeRelay relay, string screen, int dx)
    {
        int Entries() => relay.Sent.Of<EnterScreenMessage>(MessageKind.EnterScreen).Count(e => e.Screen == screen);
        var entries = Entries() + 1;
        for (var i = 0; i < 40 && Entries() < entries; i++)
            platform.FireMouseDelta(dx, 0);
        Assert.That(Entries(), Is.EqualTo(entries), $"pre-condition: on {screen}");
    }

    // from the remote across its left edge back onto the master's screen
    private static void ReturnHomeFrom(FakePlatform platform)
    {
        for (var i = 0; i < 40 && platform.IsOnVirtualScreen; i++)
            platform.FireMouseDelta(-100, 0);
        Assert.That(platform.IsOnVirtualScreen, Is.False, "pre-condition: back on the master's screen");
    }

    // -- helpers: what the master sent --

    // the hashes sent to a host the cursor was not on at the time, as host: hash
    private static List<string> OffTheCursor(FakeRelay relay)
    {
        string? on = null;
        List<string> off = [];
        foreach (var sent in relay.Sent.Snapshot().Where(s => s.Kind is MessageKind.EnterScreen or MessageKind.LeaveScreen or MessageKind.ClipboardHash))
        {
            var target = sent.Targets.Single();
            if (sent.Kind == MessageKind.EnterScreen) on = target;
            else if (sent.Kind == MessageKind.LeaveScreen && target == on) on = null;
            else if (sent.Kind == MessageKind.ClipboardHash && target != on) off.Add($"{target}: {sent.Json}");
        }
        return off;
    }

    // the streams to host that began while an older one to it was neither finished nor aborted
    private static List<long> Overlapping(FakeRelay relay, string host)
    {
        long? open = null;
        var remaining = 0;
        List<long> overlapping = [];
        foreach (var sent in relay.Sent.Snapshot().Where(s => s.Targets.Contains(host)))
        {
            switch (sent.Kind)
            {
                case MessageKind.ClipboardStreamBegin:
                    var begin = JsonSerializer.Deserialize<ClipboardStreamBeginMessage>(sent.Json, SaneJson.Options)!;
                    if (open != null) overlapping.Add(begin.Id);
                    open = begin.Id;
                    remaining = begin.TotalBytes;
                    break;
                case MessageKind.ClipboardStreamChunk:
                    var chunk = JsonSerializer.Deserialize<ClipboardStreamChunkMessage>(sent.Json, SaneJson.Options)!;
                    if (chunk.Id == open && (remaining -= chunk.Data.Length) == 0) open = null;
                    break;
                case MessageKind.ClipboardStreamAbort:
                    if (JsonSerializer.Deserialize<ClipboardStreamAbortMessage>(sent.Json, SaneJson.Options)!.Id == open) open = null;
                    break;
            }
        }
        return overlapping;
    }

    // how many of each clipboard message the master has sent
    private static Dictionary<MessageKind, int> ClipboardSent(FakeRelay relay) =>
        new[]
        {
            MessageKind.ClipboardPull, MessageKind.ClipboardHash, MessageKind.ClipboardPush, MessageKind.ClipboardStreamBegin,
            MessageKind.ClipboardStreamChunk, MessageKind.ClipboardStreamAbort,
        }.ToDictionary(k => k, k => relay.Sent.Of(k).Count);

    // -- helpers: the slaves' side --

    private TestableSlaveRelay Slave(FakeClipboardSync clipboard) => _slaves.New(clipboard, new RecordingOsd(), new FakeTimeProvider());

    // what the master sent, as the slave receives it
    private static Task Forward(TestableSlaveRelay slave, SentMessage sent) => slave.SimulateReceive(Master, sent.Kind, sent.Json);

    // a slave holding image, answering each pull once as one does: unchanged when the pull carries its clipboard's
    // hash, else its whole clipboard, streamed, aborting its older answer's stream first; one chunk at a time
    private sealed class StreamingSlave(FakeRelay relay, string host, byte[] image)
    {
        private readonly HashSet<int> _answered = [];
        private StreamedClipboard? _stream;
        private int _next;

        // how many answers it began streaming
        public int Begins { get; private set; }

        public async Task Answer(ClipboardPullMessage pull)
        {
            if (!_answered.Add(pull.PullId)) return;
            if (pull.MasterHash == ImageHash(image))
            {
                await AnswerPull(relay, new ClipboardPullResponseMessage(pull.PullId, null, Unchanged: true), host);
                return;
            }
            if (_stream != null && _next < _stream.Chunks.Length)
                await Abort(relay, _stream.Begin.Id, host);
            _stream = StreamedAnswer(++Begins, new ClipboardPullResponseMessage(pull.PullId, null, ImagePng: image));
            _next = 0;
            await Begin(relay, _stream, host);
        }

        public async Task NextChunk()
        {
            if (_stream != null && _next < _stream.Chunks.Length) await Chunk(relay, _stream.Chunks[_next++], host);
        }

        public async Task Finish()
        {
            while (_stream != null && _next < _stream.Chunks.Length) await NextChunk();
        }
    }
}

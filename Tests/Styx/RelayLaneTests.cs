using Hydra.Config;
using Hydra.Keyboard;
using Hydra.Relay;
using Tests.Setup;

namespace Tests.Styx;

/// <summary>
/// The two outbound lanes, end to end through a real Styx relay.
///
/// <para><b>Hydra is a KVM in production use, so both halves of this are load-bearing.</b> The split exists
/// so a 256 KiB file chunk stops sitting in front of every keystroke behind it — and it must buy that
/// WITHOUT loosening the ordering anything relies on, because a KeyUp delivered before its KeyDown leaves a
/// key held down on somebody's machine and nothing downstream could tell.</para>
///
/// <para>Nothing here is timed. The tests that need a lane to be slow park it on a gate and wait for
/// <c>Held</c> to say it is genuinely parked; the rest assert arrival ORDER, which is a fact rather than a
/// duration.</para>
/// </summary>
[TestFixture]
public class RelayLaneTests
{
    private static Microsoft.AspNetCore.Mvc.Testing.WebApplicationFactory<global::Styx.Program>? _factory;

    [OneTimeSetUp]
    public static void OneTimeSetUp()
    {
        _factory = StyxTestServer.Create();
        _ = _factory.Server;
    }

    [OneTimeTearDown]
    public static async Task OneTimeTearDown()
    {
        if (_factory != null) await _factory.DisposeAsync();
    }

    private static async Task<(HydraTestClient Sender, HydraTestClient Receiver)> ConnectedPair()
    {
        var cfg = await StyxTestServer.BuildNetworkConfig(_factory!, Guid.NewGuid());

        var sender = new HydraTestClient(_factory!, TransitionTestHelper.Profile("sender", new HydraConfig { Mode = Mode.Master, NetworkConfig = cfg }));
        var receiver = new HydraTestClient(_factory!, TransitionTestHelper.Profile("receiver", new HydraConfig { Mode = Mode.Master, NetworkConfig = cfg }));

        await sender.StartAsync(CancellationToken.None);
        await receiver.StartAsync(CancellationToken.None);
        await sender.WaitForReady();
        await receiver.WaitForReady();

        return (sender, receiver);
    }

    /// <summary>Everything here is bounded. A test that can hang takes the run with it.</summary>
    private static readonly TimeSpan Bound = TimeSpan.FromSeconds(30);

    private static byte[] Move(int n) => MessageSerializer.Encode(MessageKind.MouseMove, new MouseMoveMessage("", n, n));

    /// <summary>A KeyEvent, which unlike a mouse move is never coalesced — so N calls are N queue entries.</summary>
    private static byte[] Press(int n) =>
        MessageSerializer.Encode(MessageKind.KeyEvent, new KeyEventMessage(KeyEventType.KeyDown, KeyModifiers.None, (char)('a' + n % 26), null));
    private static byte[] Chunk(int n, int bytes = 8) => MessageSerializer.Encode(MessageKind.FileTransferChunk, new FileTransferChunkMessage(n, new byte[bytes]));

    /// <summary>
    /// The next message, or a failure that NAMES what never came.
    ///
    /// <para>The two tests using this are about a message arriving WHILE another is parked, so the
    /// regression they guard against shows up as nothing arriving at all — and a bare read reports that as
    /// "Timed out waiting for message", which says something is slow rather than that the two lanes have
    /// become one queue again. Measured: with the lanes collapsed both failed on that bare timeout.</para>
    ///
    /// <para>A sentinel is no help here, unlike everywhere else in these fixtures: with one queue it would
    /// be stuck behind the very item that is parked. Naming the expectation is all there is.</para>
    /// </summary>
    private static async Task<(string Source, MessageKind Kind, string Json)> NextOrFail(HydraTestClient receiver, string what)
    {
        try
        {
            return await receiver.WaitForNextMessage();
        }
        catch (TimeoutException)
        {
            Assert.Fail(what);
            throw;
        }
    }

    /// <summary>The X of a MouseMove, which is how an input message carries its position in the burst.</summary>
    private static int PositionOf(string json) =>
        System.Text.Json.JsonDocument.Parse(json).RootElement.GetProperty("x").GetInt32();

    private static int SequenceOf(string json) =>
        System.Text.Json.JsonDocument.Parse(json).RootElement.GetProperty("sequence").GetInt32();

    // ── ordering is not weakened ─────────────────────────────────────────────────────────────────

    /// <summary>
    /// A burst of input arrives in the order it was sent.
    ///
    /// <para>This is the property a KVM cannot lose. KeyDown/KeyUp, EnterScreen/LeaveScreen and
    /// button-down/up all carry their meaning in their ORDER, and none of them carries a sequence number
    /// that would let a receiver notice a swap, let alone repair one.</para>
    ///
    /// <para>MouseMove rather than KeyEvent for no reason beyond carrying an index in its X — these go
    /// through <c>SendReliableAsync</c>, which never coalesces, so the mouse path's batching is not in
    /// play. <c>AMessageBetweenTwoMouseMovesKeepsThemApart</c> is where coalescing is tested.</para>
    /// </summary>
    [Test]
    public async Task ABurstOfInputArrivesInOrder()
    {
        var (sender, receiver) = await ConnectedPair();
        await using var _ = sender;
        await using var __ = receiver;

        // NOT awaited one by one. SendReliableAsync completes only once its payload has reached the
        // wire, so awaiting each in turn puts exactly one message in flight and the arrival order is
        // guaranteed by this loop rather than by the code under test — the test would pass against a
        // lane implementation that was arbitrarily wrong. Queue them all, then wait.
        const int count = 60;
        var sent = Enumerable.Range(0, count).Select(i => sender.SendReliableAsync(["receiver"], Move(i)).AsTask()).ToArray();

        var arrived = new List<int>();
        for (var i = 0; i < count; i++) arrived.Add(PositionOf((await receiver.WaitForNextMessage()).Json));
        await Task.WhenAll(sent).WaitAsync(Bound);

        Assert.That(arrived, Is.EqualTo(Enumerable.Range(0, count)),
            "input order is the whole meaning of an input stream — a KeyUp ahead of its KeyDown sticks a key down");
    }

    /// <summary>
    /// A burst of bulk arrives in the order it was sent. The chunks feed a gzip extractor through a pipe,
    /// which cannot reassemble out of order — <c>FileTransferChunkMessage.Sequence</c> reaches the receiver
    /// but is only ever logged, so the lane is what guarantees this, not the number.
    /// </summary>
    [Test]
    public async Task ABurstOfBulkArrivesInOrder()
    {
        var (sender, receiver) = await ConnectedPair();
        await using var _ = sender;
        await using var __ = receiver;

        // Queued all at once, for the reason ABurstOfInputArrivesInOrder gives.
        const int count = 60;
        var sent = Enumerable.Range(0, count).Select(i => sender.SendReliableAsync(["receiver"], Chunk(i)).AsTask()).ToArray();

        var arrived = new List<int>();
        for (var i = 0; i < count; i++) arrived.Add(SequenceOf((await receiver.WaitForNextMessage()).Json));
        await Task.WhenAll(sent).WaitAsync(Bound);

        Assert.That(arrived, Is.EqualTo(Enumerable.Range(0, count)));
    }

    /// <summary>
    /// With both lanes busy at once, each keeps its OWN order. Nothing is claimed about how the two
    /// interleave — that is exactly what the split gives up, and it is safe to give up because a keystroke
    /// and a file chunk have no relationship to each other.
    /// </summary>
    [Test]
    public async Task TwoBusyLanesEachKeepTheirOwnOrder()
    {
        var (sender, receiver) = await ConnectedPair();
        await using var _ = sender;
        await using var __ = receiver;

        // Every message queued before anything is awaited, so both channels genuinely hold a backlog and
        // the two drains really do run at once. Awaiting each send in turn would serialise the whole test
        // and prove nothing about either lane.
        const int count = 40;
        var sent = new List<Task>();
        for (var i = 0; i < count; i++)
        {
            sent.Add(sender.SendReliableAsync(["receiver"], Chunk(i, 4096)).AsTask());
            sent.Add(sender.SendReliableAsync(["receiver"], Move(i)).AsTask());
        }

        var input = new List<int>();
        var bulk = new List<int>();
        for (var i = 0; i < count * 2; i++)
        {
            var (_, kind, json) = await receiver.WaitForNextMessage();
            if (kind == MessageKind.FileTransferChunk) bulk.Add(SequenceOf(json));
            else input.Add(PositionOf(json));
        }

        await Task.WhenAll(sent).WaitAsync(Bound);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(input, Is.EqualTo(Enumerable.Range(0, count)), "the input lane's own order");
            Assert.That(bulk, Is.EqualTo(Enumerable.Range(0, count)), "the bulk lane's own order");
        }
    }

    // ── and this is what it bought ───────────────────────────────────────────────────────────────

    /// <summary>
    /// <b>The reason the split exists.</b> A keystroke sent AFTER a stalled file chunk arrives while that
    /// chunk is still stuck — which on one queue was impossible by construction.
    ///
    /// <para>Deterministic: the bulk lane is parked on a gate and the test waits for <c>Held</c> to
    /// confirm it before sending the keystroke, so the assertion does not depend on a chunk being slower
    /// than a keypress on this particular machine. On the single-queue code this test cannot pass — the
    /// keystroke is queued behind the held chunk and never arrives.</para>
    /// </summary>
    [Test]
    public async Task AKeystrokeOvertakesAStalledFileChunk()
    {
        var (sender, receiver) = await ConnectedPair();
        await using var _ = sender;
        await using var __ = receiver;

        sender.HoldLane(RelayLane.Bulk);

        // NOT awaited: a reliable send completes only once the payload has actually left, and this one is
        // about to be parked on the gate. Waiting for Held is what says it is queued and stuck.
        var stalled = sender.SendReliableAsync(["receiver"], Chunk(7)).AsTask();
        await WaitFor(() => sender.Held == 1, "the bulk lane to park on the gate");

        sender.Send(["receiver"], Move(1));

        var (_, kind, _) = await NextOrFail(receiver,
            "nothing arrived at all while the chunk was parked — the keystroke is queued behind it, so the lanes are one queue again");
        Assert.That(kind, Is.EqualTo(MessageKind.MouseMove),
            "the keystroke was sent second and must arrive first — the whole point of taking bulk off the input lane");

        sender.ReleaseLane();

        var (_, secondKind, _) = await receiver.WaitForNextMessage();
        Assert.That(secondKind, Is.EqualTo(MessageKind.FileTransferChunk), "and the chunk still arrives once released");
        await stalled.WaitAsync(Bound);
    }

    /// <summary>
    /// The stream's own three kinds keep their order even while input floods the other lane — they share a
    /// lane precisely so this holds. A Start that lost its race with chunk 0 would be dropped in silence by
    /// <c>HandleFileTransferChunkAsync</c>'s null-extractor guard; a Done that overtook the last chunk would
    /// truncate the transfer and fail its hash.
    /// </summary>
    [Test]
    public async Task TheTransferStreamKeepsItsOrderWhileInputFloodsTheOtherLane()
    {
        var (sender, receiver) = await ConnectedPair();
        await using var _ = sender;
        await using var __ = receiver;

        // KeyEvent, not MouseMove: movement COALESCES, so a flood of moves collapses to a handful of
        // queue entries and the input lane is never actually loaded.
        //
        // And the whole stream is queued before anything is awaited. Awaiting each part in turn would put
        // one message in flight at a time, and then this test passes with Start on the input lane and the
        // chunks on the bulk one — the very split its own summary says must never happen.
        const int chunks = 25;
        var sent = new List<Task>();
        for (var i = 0; i < 200; i++) sent.Add(sender.SendReliableAsync(["receiver"], Press(i)).AsTask());

        sent.Add(sender.SendReliableAsync(["receiver"], MessageSerializer.Encode(MessageKind.FileTransferStart, new FileTransferStartMessage(["f"], 100))).AsTask());
        for (var i = 0; i < chunks; i++) sent.Add(sender.SendReliableAsync(["receiver"], Chunk(i, 2048)).AsTask());
        sent.Add(sender.SendReliableAsync(["receiver"], MessageSerializer.Encode(MessageKind.FileTransferDone, new FileTransferDoneMessage(100, new byte[32]))).AsTask());

        var stream = new List<MessageKind>();
        while (stream.Count < chunks + 2)
        {
            var (_, kind, _) = await receiver.WaitForNextMessage();
            if (kind is MessageKind.FileTransferStart or MessageKind.FileTransferChunk or MessageKind.FileTransferDone)
                stream.Add(kind);
        }

        await Task.WhenAll(sent).WaitAsync(Bound);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(stream[0], Is.EqualTo(MessageKind.FileTransferStart), "Start must lead, or the receiver has no extractor yet");
            Assert.That(stream[^1], Is.EqualTo(MessageKind.FileTransferDone), "Done must trail, or the transfer is finalised short");
            Assert.That(stream.Count(k => k == MessageKind.FileTransferChunk), Is.EqualTo(chunks));
        }
    }

    /// <summary>
    /// An abort overtakes the chunks it is cancelling, which is the point of leaving it on the control lane:
    /// waiting out the backlog it exists to stop would be the wrong behaviour.
    /// </summary>
    [Test]
    public async Task AnAbortOvertakesTheChunksItCancels()
    {
        var (sender, receiver) = await ConnectedPair();
        await using var _ = sender;
        await using var __ = receiver;

        sender.HoldLane(RelayLane.Bulk);
        var held = sender.SendReliableAsync(["receiver"], Chunk(1)).AsTask();
        await WaitFor(() => sender.Held == 1, "the bulk lane to park");

        sender.Send(["receiver"], MessageSerializer.Encode(MessageKind.FileTransferAbort, new FileTransferAbortMessage("cancelled")));

        var (_, kind, _) = await NextOrFail(receiver,
            "the abort never arrived while the chunk it cancels was parked — it is queued behind the backlog it exists to stop");
        Assert.That(kind, Is.EqualTo(MessageKind.FileTransferAbort));

        sender.ReleaseLane();
        await held.WaitAsync(Bound);
    }

    /// <summary>
    /// A message between two mouse moves keeps them apart.
    ///
    /// <para><b>This is why a click lands where the cursor actually was.</b> Moves coalesce, so without
    /// something closing the open batch a move made before a keypress and one made after it would merge
    /// into a single position — and the press would be delivered against the wrong one. The batch is closed
    /// by every enqueue, on either lane, for the reason <c>Send</c> states.</para>
    ///
    /// <para><b>The drain has to be PARKED for this to test anything, and parking it on the move itself is
    /// no good.</b> <c>TryReadQueued</c> closes the batch when it READS an item, so an unparked lane closes
    /// it before the second move is ever made and the test passes with the close deleted — which is exactly
    /// the defect this test replaced. So the lane is parked on a message AHEAD of the first move, leaving
    /// the batch genuinely open across the separator.</para>
    /// </summary>
    [Test]
    public async Task AMessageBetweenTwoMouseMovesKeepsThemApart()
    {
        var (sender, receiver) = await ConnectedPair();
        await using var _ = sender;
        await using var __ = receiver;

        // Park the input drain on something ahead of everything below, so nothing is read while the batch
        // is being built and the close is the ONLY thing that can separate the two moves.
        sender.HoldLane(RelayLane.Input);
        var blocker = sender.SendReliableAsync(["receiver"], Press(9)).AsTask();
        await WaitFor(() => sender.Held == 1, "the input lane to park");

        sender.Send(["receiver"], Move(1));
        var separator = sender.SendReliableAsync(["receiver"], Press(0)).AsTask();
        sender.Send(["receiver"], Move(2));

        // A SENTINEL, so the count is read off what actually arrived. Waiting for a fixed number of
        // messages would make a merge — which produces one FEWER — fail by timing out on a message that
        // was never coming, and a timeout says "something is slow" where this needs to say "the moves
        // merged". It is enqueued last and cannot coalesce, so it is always the final arrival.
        var sentinel = sender.SendReliableAsync(["receiver"], Press(1)).AsTask();

        sender.ReleaseLane();
        await Task.WhenAll(blocker, separator, sentinel).WaitAsync(Bound);

        var arrived = new List<MessageKind>();
        while (arrived.Count(k => k == MessageKind.KeyEvent) < 3)
            arrived.Add((await receiver.WaitForNextMessage()).Kind);

        Assert.That(arrived.Count(k => k == MessageKind.MouseMove), Is.EqualTo(2),
            "the two moves merged across a keypress — the press would be delivered at the wrong position");
    }

    // ── a dropped connection must not strand a caller ────────────────────────────────────────────

    /// <summary>
    /// Everything queued on EITHER lane is failed when the connection goes, so nobody is left awaiting a
    /// send that will never happen.
    ///
    /// <para><b>This is a hang, not a wrong answer, and it is the specific thing the split could have
    /// broken.</b> <c>SendReliableAsync</c> returns a task completed only by the drain loop, and the
    /// disconnect path used to empty the one queue there was. With two lanes it has to empty both — and the
    /// bulk lane is precisely the one whose callers actually await, because that is how
    /// <c>FileTransferService</c> paces a transfer. A lane left unemptied is a file transfer that never
    /// returns and a dialog that never closes, until the process is killed.</para>
    /// </summary>
    [TestCase(RelayLane.Bulk)]
    [TestCase(RelayLane.Input)]
    public async Task ADroppedConnectionFailsWhatIsQueuedOnALane(RelayLane lane)
    {
        var (sender, receiver) = await ConnectedPair();
        await using var _ = sender;
        await using var __ = receiver;

        // BOTH lanes are exercised, one per case. Holding only the bulk one would leave
        // FailQueued(_inputQueue.Reader) uncovered — deleting that line would still pass.
        sender.HoldLane(lane);
        var payload = lane == RelayLane.Bulk ? Chunk(1) : Move(1);

        // One parks inside the lane's encrypt step; the next stays in the channel behind it. The two are
        // emptied by different code, so both have to be here.
        var parked = sender.SendReliableAsync(["receiver"], payload).AsTask();
        await WaitFor(() => sender.Held == 1, $"the {lane} lane to park");
        var behind = sender.SendReliableAsync(["receiver"], payload).AsTask();

        // BOUNDED. If the drain ever goes back to encrypting on the app-lifetime token, the parked lane
        // never unwinds, the iteration never ends, and this call waits for ever — turning the regression
        // this test exists for into a wedged run rather than a failure.
        using var bound = new CancellationTokenSource(Bound);
        await sender.SuspendConnectionAsync(bound.Token);

        foreach (var (task, where) in new[] { (parked, "parked in the lane"), (behind, "queued behind it") })
        {
            var settled = await Task.WhenAny(task, Task.Delay(Bound));
            using (Assert.EnterMultipleScope())
            {
                Assert.That(settled, Is.SameAs(task), $"a caller {where} was left waiting on a send that can never happen");
                Assert.That(task.IsCompletedSuccessfully, Is.False, $"the payload {where} never left, so it must not report success");
            }
        }

        sender.ReleaseLane();
    }

    private static async Task WaitFor(Func<bool> condition, string what, int timeoutMs = 15000)
    {
        using var cancel = new CancellationTokenSource(timeoutMs);
        try
        {
            while (!condition()) await Task.Delay(10, cancel.Token);
        }
        catch (OperationCanceledException)
        {
            Assert.Fail($"Timed out waiting for {what}");
        }
    }

    /// <summary>
    /// The relay must dispatch at least as many of one connection's invocations at once as that connection
    /// has lanes.
    ///
    /// <para><b>Nothing else in this file can see this.</b> Every lane test here parks on the client's own
    /// encrypt gate, so they all stay green with the relay dispatching one invocation at a time — and at
    /// one, a 256 KiB chunk's invocation occupies the only slot and the keystroke behind it is not even
    /// dispatched. That is the head-of-line blocking this whole change removed, rebuilt at the relay, with
    /// the client-side split still looking perfectly correct.</para>
    ///
    /// <para>Counted from <c>RelayLane</c> rather than written as 2, so adding a third lane fails here
    /// instead of quietly re-introducing the stall.</para>
    /// </summary>
    [Test]
    public void TheRelayDispatchesAtLeastAsManyInvocationsAsAPeerHasLanes()
    {
        var slots = global::Styx.Constants.MaxParallelInvocations;
        Assert.That(slots, Is.GreaterThanOrEqualTo(Enum.GetValues<RelayLane>().Length),
            "a lane's invocation is not finished until the hub method returns, so fewer slots than lanes means a lane waits for another lane's frame to be delivered");
    }
}

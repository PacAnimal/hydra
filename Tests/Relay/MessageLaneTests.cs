using Hydra.Relay;

namespace Tests.Relay;

/// <summary>
/// Which lane each message kind travels on.
///
/// <para><b>Hydra is a KVM in production use, and this table decides what can overtake what.</b> Put a kind
/// on the bulk lane and it may now arrive after input sent later; take one off and it queues behind 256 KiB
/// file chunks again. Neither mistake produces a compiler error, and neither is visible in a code review of
/// the enum — so the mapping is asserted here, exhaustively, rather than trusted.</para>
/// </summary>
[TestFixture]
public class MessageLaneTests
{
    /// <summary>
    /// The three kinds that carry the file-transfer STREAM, and nothing else.
    ///
    /// <para>They are together because they are strictly ordered with respect to one another:
    /// <c>FileTransferStart</c> creates the receiver's extractor, the chunks feed it, and
    /// <c>FileTransferDone</c> finalises and verifies the hash.</para>
    /// </summary>
    private static readonly MessageKind[] Bulk =
    [
        MessageKind.FileTransferStart,
        MessageKind.FileTransferChunk,
        MessageKind.FileTransferDone
    ];

    /// <summary>
    /// EVERY declared kind is classified, and adding one to the enum fails this until somebody decides.
    ///
    /// <para><b>This is the guard that matters.</b> <c>MessageLane.Of</c> ends in <c>_ =&gt; Input</c>, which
    /// is the safe default — the ordered lane, behaving exactly as everything did before there were two — so
    /// a new kind cannot break anything by being forgotten. It can, however, be forgotten SILENTLY: a future
    /// streaming kind added beside <c>FileTransferChunk</c> would take the input lane and put 256 KiB
    /// payloads back in front of every keystroke, and nothing would say so. This test is what says so.</para>
    /// </summary>
    [Test]
    public void EveryMessageKindIsDeliberatelyAssignedToALane()
    {
        var unclassified = Enum.GetValues<MessageKind>()
            .Where(kind => (MessageLane.Of(kind) == RelayLane.Bulk) != Bulk.Contains(kind))
            .ToArray();

        Assert.That(unclassified, Is.Empty,
            "a message kind's lane changed, or a new kind was added. Decide which lane it belongs on — bulk is "
            + "for a stream whose parts must stay ordered with each other and may lag input; input is everything "
            + "else — then say so in this test's Bulk list as well as in MessageLane.Of");
    }

    [Test]
    public void TheFileTransferStreamIsBulk()
    {
        using (Assert.EnterMultipleScope())
        {
            foreach (var kind in Bulk)
                Assert.That(MessageLane.Of(kind), Is.EqualTo(RelayLane.Bulk), $"{kind} carries the stream");
        }
    }

    /// <summary>
    /// Input keeps the kinds whose latency is the product, and the ones whose ordering against input matters.
    /// </summary>
    [TestCase(MessageKind.KeyEvent)]
    [TestCase(MessageKind.MouseMove)]
    [TestCase(MessageKind.MouseMoveDelta)]
    [TestCase(MessageKind.MouseButton)]
    [TestCase(MessageKind.MouseScroll)]
    [TestCase(MessageKind.EnterScreen)]
    [TestCase(MessageKind.LeaveScreen)]
    [TestCase(MessageKind.LockScreen)]
    [TestCase(MessageKind.ClipboardPush)]
    public void InputAndControlStayOnTheInputLane(MessageKind kind) =>
        Assert.That(MessageLane.Of(kind), Is.EqualTo(RelayLane.Input));

    /// <summary>
    /// An abort is control, not stream, and that is deliberate: it means STOP, so it should overtake the
    /// chunks still queued rather than wait out the backlog it is trying to cancel. Arriving early is safe —
    /// the receiver tears down, and a chunk landing afterwards finds no extractor and is discarded, which is
    /// exactly what an abort asked for.
    /// </summary>
    [Test]
    public void AnAbortIsControlSoItCanOvertakeTheChunksItCancels() =>
        Assert.That(MessageLane.Of(MessageKind.FileTransferAbort), Is.EqualTo(RelayLane.Input));

    /// <summary>
    /// The negotiation around a transfer is control too. Each is one half of a round trip that completes
    /// before any stream starts, so it is ordered by the reply it waits for rather than by the lane.
    /// </summary>
    [TestCase(MessageKind.FileTransferRequest)]
    [TestCase(MessageKind.FileTransferAccepted)]
    [TestCase(MessageKind.FileTransferBusy)]
    [TestCase(MessageKind.FileSelectionQuery)]
    [TestCase(MessageKind.FileSelectionResponse)]
    [TestCase(MessageKind.FileStreamRequest)]
    public void TransferNegotiationIsControl(MessageKind kind) =>
        Assert.That(MessageLane.Of(kind), Is.EqualTo(RelayLane.Input));

    // ── the payload overload ─────────────────────────────────────────────────────────────────────

    [Test]
    public void AnEncodedPayloadIsClassifiedByItsFirstByte()
    {
        var chunk = MessageSerializer.Encode(MessageKind.FileTransferChunk, new FileTransferChunkMessage(0, [1, 2, 3]));
        var key = MessageSerializer.Encode(MessageKind.MouseMove, new MouseMoveMessage("", 1, 2));

        using (Assert.EnterMultipleScope())
        {
            Assert.That(MessageLane.Of(chunk), Is.EqualTo(RelayLane.Bulk));
            Assert.That(MessageLane.Of(key), Is.EqualTo(RelayLane.Input));
        }
    }

    /// <summary>
    /// An empty payload has no kind byte to read. Input, because that is the ordered lane and behaves as
    /// everything did before the split — the safe direction to be wrong in.
    /// </summary>
    [Test]
    public void AnEmptyPayloadIsInput() =>
        Assert.That(MessageLane.Of([]), Is.EqualTo(RelayLane.Input));

    /// <summary>A byte that is no declared kind at all — a peer on a newer build — is input for the same reason.</summary>
    [Test]
    public void AnUnknownKindIsInput() =>
        Assert.That(MessageLane.Of([0xFE, 1, 2]), Is.EqualTo(RelayLane.Input));
}

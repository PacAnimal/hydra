using Hydra.Relay;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Time.Testing;
using Tests.Setup;

namespace Tests.Relay;

[TestFixture]
public class ClipboardStreamReceiverTests
{
    private const ulong ContentHash = 0xC0FFEE;
    private const int ChunkBytes = 100;

    private FakeClipboardSync _sync = null!;
    private FakeTimeProvider _clock = null!;
    private ClipboardStreamReceiver _receiver = null!;
    private List<CompletedStream> _completed = null!;
    private List<StreamRef> _abandoned = null!;
    private List<StreamRef> _failed = null!;
    private CapturingLog<ClipboardStreamReceiver> _log = null!;
    private int _clears;

    [SetUp]
    public void SetUp()
    {
        _sync = new FakeClipboardSync();
        _clock = new FakeTimeProvider();
        _clears = 0;
        _log = new CapturingLog<ClipboardStreamReceiver>(LogLevel.Warning);
        _completed = [];
        _abandoned = [];
        _failed = [];
        _receiver = NewReceiver(MessageKind.ClipboardPush);
    }

    // replaces the fixture's receiver with one taking only accepts
    private void Accepting(MessageKind accepts)
    {
        _receiver.Dispose();
        _receiver = NewReceiver(accepts);
    }

    private ClipboardStreamReceiver NewReceiver(MessageKind accepts)
    {
        var receiver = new ClipboardStreamReceiver(_sync, (_, _) => Clear(), _ => Clear(), _clock, _log, accepts);
        receiver.Abandoned += (host, id) => _abandoned.Add(new StreamRef(host, id));
        receiver.Failed += failed => _failed.Add(new StreamRef(failed.Host, failed.Id));
        return receiver;
    }

    private BeginDecision Clear()
    {
        _clears++;
        return BeginDecision.Cleared;
    }

    [TearDown]
    public void TearDown() => _receiver.Dispose();

    [Test]
    public void BeginClearsOnce_ChunksInOrderComplete()
    {
        var push = Push('x');
        var stream = Split(1, Encode(push));

        _receiver.OnBegin("a", stream.Begin);
        Assert.That(_clears, Is.EqualTo(1));
        foreach (var chunk in stream.Chunks) Chunk("a", chunk);

        Assert.That(_clears, Is.EqualTo(1));
        var completed = _completed.Single();
        using (Assert.EnterMultipleScope())
        {
            Assert.That(completed.Host, Is.EqualTo("a"));
            Assert.That(completed.Message.Kind, Is.EqualTo(MessageKind.ClipboardPush));
            Assert.That(completed.Message.Deserialize<ClipboardPushMessage>(), Is.EqualTo(push));
            Assert.That(_receiver.PendingFrom("a"), Is.Null);
            Assert.That(_failed, Is.Empty);
        }
    }

    [Test]
    public void PullResponse_Completes()
    {
        Accepting(MessageKind.ClipboardPullResponse);
        var response = new ClipboardPullResponseMessage(7, new string('y', 450));
        var stream = Split(1, MessageSerializer.Encode(MessageKind.ClipboardPullResponse, response), inner: MessageKind.ClipboardPullResponse);

        Deliver("a", stream);

        var completed = _completed.Single();
        using (Assert.EnterMultipleScope())
        {
            Assert.That(completed.Message.Kind, Is.EqualTo(MessageKind.ClipboardPullResponse));
            Assert.That(completed.Message.Deserialize<ClipboardPullResponseMessage>(), Is.EqualTo(response));
        }
    }

    [Test]
    public void NothingCompletesBeforeTheLastChunk_PendingMeanwhile()
    {
        var stream = Split(1, Encode(Push('x')));
        Assert.That(stream.Chunks, Has.Length.GreaterThan(2));

        _receiver.OnBegin("a", stream.Begin);
        foreach (var chunk in stream.Chunks[..^1])
        {
            using (Assert.EnterMultipleScope())
            {
                Assert.That(_receiver.PendingFrom("a"), Is.EqualTo(ContentHash));
            }
            Chunk("a", chunk);
            Assert.That(_completed, Is.Empty);
        }

        using (Assert.EnterMultipleScope())
        {
            Assert.That(_receiver.PendingFrom("a"), Is.EqualTo(ContentHash));
        }

        Chunk("a", stream.Chunks[^1]);

        Assert.That(_completed, Has.Count.EqualTo(1));
    }

    [Test]
    public void ChunkWithAnotherId_IsDropped_StreamStillCompletes()
    {
        var stream = Split(1, Encode(Push('x')));

        _receiver.OnBegin("a", stream.Begin);
        Chunk("a", stream.Chunks[0]);
        Chunk("a", stream.Chunks[1] with { Id = 2 });
        foreach (var chunk in stream.Chunks[1..]) Chunk("a", chunk);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(_completed, Has.Count.EqualTo(1));
            Assert.That(_failed, Is.Empty);
        }
    }

    [Test]
    public void ChunkWithNoBegin_IsDropped()
    {
        var stream = Split(1, Encode(Push('x')));

        foreach (var chunk in stream.Chunks) Chunk("a", chunk);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(_completed, Is.Empty);
            Assert.That(_failed, Is.Empty);
            Assert.That(_clears, Is.Zero);
        }
    }

    [Test]
    public void NewBegin_ReplacesTheOldStream_StaleChunkDropped()
    {
        var old = Split(1, Encode(Push('x')));
        var push = Push('z');
        var fresh = Split(2, Encode(push), contentHash: ContentHash + 1);

        _receiver.OnBegin("a", old.Begin);
        Chunk("a", old.Chunks[0]);
        _receiver.OnBegin("a", fresh.Begin);
        Chunk("a", old.Chunks[1]);
        using (Assert.EnterMultipleScope())
        {
            Assert.That(_receiver.PendingFrom("a"), Is.EqualTo(ContentHash + 1));
        }
        foreach (var chunk in fresh.Chunks) Chunk("a", chunk);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(_clears, Is.EqualTo(2));
            Assert.That(_completed.Single().Message.Deserialize<ClipboardPushMessage>(), Is.EqualTo(push));
            Assert.That(_failed, Is.Empty);
        }
    }

    [Test]
    public void WrongOffset_Fails()
    {
        var stream = Split(1, Encode(Push('x')));

        _receiver.OnBegin("a", stream.Begin);
        Chunk("a", stream.Chunks[0]);
        Chunk("a", stream.Chunks[2]);
        foreach (var chunk in stream.Chunks[1..]) Chunk("a", chunk);

        AssertFailedOnce("a");
    }

    [Test]
    public void ChunkPastTheTotal_Fails()
    {
        var blob = Encode(Push('x'));
        var stream = Split(1, blob);

        _receiver.OnBegin("a", stream.Begin);
        foreach (var chunk in stream.Chunks[..^1]) Chunk("a", chunk);
        var last = stream.Chunks[^1];
        Chunk("a", last with { Data = [.. last.Data, 0] });

        AssertFailedOnce("a");
    }

    [Test]
    public void BlobHashMismatch_Fails()
    {
        var stream = Split(1, Encode(Push('x')), blobHash: 12345);

        Deliver("a", stream);

        AssertFailedOnce("a");
    }

    [Test]
    public void InnerMismatch_Fails()
    {
        Accepting(MessageKind.ClipboardPullResponse);
        var stream = Split(1, Encode(Push('x')), inner: MessageKind.ClipboardPullResponse);

        Deliver("a", stream);

        AssertFailedOnce("a");
    }

    // a rejected Begin still told the receiver a newer clipboard is coming, so it empties
    [TestCase(MessageKind.ClipboardHash)]
    [TestCase(MessageKind.ClipboardStreamBegin)]
    [TestCase(MessageKind.ClipboardPullResponse)]
    public void InnerNotTheAcceptedKind_FailsAtBegin_AndStillClears(MessageKind inner)
    {
        var blob = MessageSerializer.Encode(inner, new ClipboardHashMessage(1));
        var stream = Split(1, blob, inner: inner);

        Deliver("a", stream);

        AssertFailedOnce("a");
        Assert.That(_clears, Is.EqualTo(1));
    }

    [Test]
    public void APushToTheMastersReceiver_FailsAtBegin_AndStillClears()
    {
        Accepting(MessageKind.ClipboardPullResponse);

        Deliver("a", Split(4, Encode(Push('x'))));

        AssertFailedOnce("a", 4);
        Assert.That(_clears, Is.EqualTo(1));
    }

    [TestCase(0)]
    [TestCase(-1)]
    [TestCase(int.MaxValue)]
    public void TotalOutOfRange_FailsAtBegin_AndStillClears(int total)
    {
        var stream = Split(1, Encode(Push('x')));

        _receiver.OnBegin("a", stream.Begin with { TotalBytes = total });

        AssertFailedOnce("a");
        Assert.That(_clears, Is.EqualTo(1));
    }

    [Test]
    public void ARejectedBegin_DropsTheOlderStreamFromThatHost_AndNoOther()
    {
        var old = Split(1, Encode(Push('x')));
        var other = Split(1, Encode(Push('o')), contentHash: 2);
        _receiver.OnBegin("a", old.Begin);
        _receiver.OnBegin("b", other.Begin);
        Chunk("a", old.Chunks[0]);

        _receiver.OnBegin("a", old.Begin with { Id = 2, TotalBytes = 0 });
        foreach (var chunk in old.Chunks[1..]) Chunk("a", chunk);
        foreach (var chunk in other.Chunks) Chunk("b", chunk);
        _clock.Advance(ClipboardStreamReceiver.Watchdog * 2);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(_failed, Is.EqualTo([new StreamRef("a", 2)]), "only the rejected Begin fails, never the stream it replaced");
            Assert.That(_receiver.PendingFrom("a"), Is.Null);
            Assert.That(_completed.Select(c => c.Host), Is.EqualTo(["b"]));
            Assert.That(_clears, Is.EqualTo(3));
        }
    }

    [TestCase(false)]
    [TestCase(true)]
    public void AnEmptyChunk_FailsTheStream(bool nullData)
    {
        var stream = Split(1, Encode(Push('x')));

        _receiver.OnBegin("a", stream.Begin);
        Chunk("a", stream.Chunks[0]);
        Chunk("a", stream.Chunks[1] with { Data = nullData ? null! : [] });
        foreach (var chunk in stream.Chunks[1..]) Chunk("a", chunk);

        AssertFailedOnce("a");
    }

    [Test]
    public void EmptyChunks_NeverKeepAStreamAlive()
    {
        var stream = Split(1, Encode(Push('x')));
        _receiver.OnBegin("a", stream.Begin);

        _clock.Advance(ClipboardStreamReceiver.Watchdog - TimeSpan.FromSeconds(1));
        Chunk("a", stream.Chunks[0] with { Data = [] });
        _clock.Advance(ClipboardStreamReceiver.Watchdog * 2);

        AssertFailedOnce("a");
    }

    [Test]
    public void ACompletedMessageThatWouldNotParse_FailsWithItsId()
    {
        _receiver.OnUnreadable("a", 9);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(_failed, Is.EqualTo([new StreamRef("a", 9)]));
            AssertWarnedOnce("a");
        }
    }

    [Test]
    public void TheWatchdog_OutlastsTheRelaysStallDrop()
    {
        var deadline = TimeSpan.FromSeconds(global::Styx.Constants.PeerWriteDeadlineSeconds);
        Assert.That(ClipboardStreamReceiver.Watchdog, Is.EqualTo(deadline + TimeSpan.FromSeconds(15)));
    }

    [Test]
    public void AfterDispose_EverythingIsIgnored()
    {
        var stream = Split(1, Encode(Push('x')));
        _receiver.OnBegin("a", stream.Begin);
        Chunk("a", stream.Chunks[0]);
        var clears = _clears;

        _receiver.Dispose();
        _receiver.OnBegin("b", Split(2, Encode(Push('y'))).Begin);
        _receiver.OnBegin("c", Split(4, Encode(Push('c'))).Begin with { TotalBytes = 0 });
        foreach (var chunk in stream.Chunks[1..]) Chunk("a", chunk);
        _sync.CanUseEchoFallbackValue = false;
        _receiver.OnBegin("d", Split(3, Encode(Push('z'))).Begin);
        Chunk("d", Split(3, Encode(Push('z'))).Chunks[0]);
        _receiver.OnUnreadable("a", 1);
        _receiver.OnAbort("a", new ClipboardStreamAbortMessage(1));
        _clock.Advance(ClipboardStreamReceiver.Watchdog * 2);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(_clears, Is.EqualTo(clears), "nothing more cleared");
            Assert.That(_failed, Is.Empty);
            Assert.That(_abandoned, Is.Empty);
            Assert.That(_completed, Is.Empty);
            Assert.That(_receiver.PendingFrom("b"), Is.Null, "no stream begun, so no timer armed");
        }
    }

    // the buffer grows and the hash is taken a chunk at a time, whatever the chunks' sizes
    [Test]
    public void UnevenChunks_LandWhole()
    {
        var push = new ClipboardPushMessage(string.Concat(Enumerable.Range(0, 450).Select(i => (char)('a' + i % 26))));
        var blob = Encode(push);
        var stream = Split(1, blob);
        int[] cuts = [0, 1, 8, 300, blob.Length];

        _receiver.OnBegin("a", stream.Begin);
        for (var i = 0; i + 1 < cuts.Length; i++)
            Chunk("a", new ClipboardStreamChunkMessage(1, cuts[i], blob[cuts[i]..cuts[i + 1]]));

        using (Assert.EnterMultipleScope())
        {
            Assert.That(_failed, Is.Empty);
            Assert.That(_completed.Single().Message.Deserialize<ClipboardPushMessage>(), Is.EqualTo(push));
        }
    }

    [Test]
    public void ClipboardNoLongerOurs_Abandons_WithoutFailing()
    {
        var stream = Split(7, Encode(Push('x')));

        _receiver.OnBegin("a", stream.Begin);
        Chunk("a", stream.Chunks[0]);
        _sync.CanUseEchoFallbackValue = false;
        foreach (var chunk in stream.Chunks[1..]) Chunk("a", chunk);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(_abandoned, Is.EqualTo([new StreamRef("a", 7)]));
            Assert.That(_completed, Is.Empty);
            Assert.That(_failed, Is.Empty);
            Assert.That(_receiver.PendingFrom("a"), Is.Null);
        }
    }

    [Test]
    public void AbortWithTheCurrentId_DropsItWithoutFailing()
    {
        var stream = Split(3, Encode(Push('x')));

        _receiver.OnBegin("a", stream.Begin);
        Chunk("a", stream.Chunks[0]);
        _receiver.OnAbort("a", new ClipboardStreamAbortMessage(3));
        foreach (var chunk in stream.Chunks[1..]) Chunk("a", chunk);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(_failed, Is.Empty);
            Assert.That(_completed, Is.Empty);
            Assert.That(_abandoned, Is.Empty);
            Assert.That(_receiver.PendingFrom("a"), Is.Null);
        }
    }

    [Test]
    public void AFailedAbortWithTheCurrentId_FailsTheStream_AndWarns()
    {
        var stream = Split(3, Encode(Push('x')));

        _receiver.OnBegin("a", stream.Begin);
        Chunk("a", stream.Chunks[0]);
        _receiver.OnAbort("a", new ClipboardStreamAbortMessage(3, Failed: true));
        foreach (var chunk in stream.Chunks[1..]) Chunk("a", chunk);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(_failed, Is.EqualTo([new StreamRef("a", 3)]));
            Assert.That(_log.Entries.Select(e => e.Message), Has.Some.Contains("a").And.Contains("gave up"));
            Assert.That(_completed, Is.Empty);
            Assert.That(_abandoned, Is.Empty);
            Assert.That(_receiver.PendingFrom("a"), Is.Null);
        }
    }

    [Test]
    public void AFailedAbortWithAnotherId_IsIgnored()
    {
        var stream = Split(3, Encode(Push('x')));

        _receiver.OnBegin("a", stream.Begin);
        _receiver.OnAbort("a", new ClipboardStreamAbortMessage(2, Failed: true));
        foreach (var chunk in stream.Chunks) Chunk("a", chunk);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(_completed, Has.Count.EqualTo(1));
            Assert.That(_failed, Is.Empty);
        }
    }

    [Test]
    public void AbortWithAnotherId_IsIgnored()
    {
        var stream = Split(3, Encode(Push('x')));

        _receiver.OnBegin("a", stream.Begin);
        _receiver.OnAbort("a", new ClipboardStreamAbortMessage(2));
        _receiver.OnAbort("b", new ClipboardStreamAbortMessage(3));
        foreach (var chunk in stream.Chunks) Chunk("a", chunk);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(_completed, Has.Count.EqualTo(1));
            Assert.That(_failed, Is.Empty);
        }
    }

    [Test]
    public void Watchdog_FailsAStreamWithNoChunk()
    {
        var stream = Split(1, Encode(Push('x')));

        _receiver.OnBegin("a", stream.Begin);
        Chunk("a", stream.Chunks[0]);
        _clock.Advance(ClipboardStreamReceiver.Watchdog + TimeSpan.FromSeconds(1));

        using (Assert.EnterMultipleScope())
        {
            Assert.That(_failed, Is.EqualTo([new StreamRef("a", 1)]));
            Assert.That(_receiver.PendingFrom("a"), Is.Null);
        }
        foreach (var chunk in stream.Chunks[1..]) Chunk("a", chunk);
        using (Assert.EnterMultipleScope())
        {
            Assert.That(_completed, Is.Empty);
            Assert.That(_failed, Has.Count.EqualTo(1));
            AssertWarnedOnce("a");
        }
    }

    [Test]
    public void Watchdog_IsResetByEachChunk()
    {
        var stream = Split(1, Encode(Push('x')));
        var almost = ClipboardStreamReceiver.Watchdog - TimeSpan.FromSeconds(1);

        _receiver.OnBegin("a", stream.Begin);
        foreach (var chunk in stream.Chunks)
        {
            _clock.Advance(almost);
            Chunk("a", chunk);
        }

        using (Assert.EnterMultipleScope())
        {
            Assert.That(_failed, Is.Empty);
            Assert.That(_completed, Has.Count.EqualTo(1));
        }
    }

    [Test]
    public void Watchdog_DoesNotFireAfterCompletion()
    {
        Deliver("a", Split(1, Encode(Push('x'))));

        _clock.Advance(ClipboardStreamReceiver.Watchdog * 2);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(_failed, Is.Empty);
            Assert.That(_completed, Has.Count.EqualTo(1));
        }
    }

    [Test]
    public void TwoHosts_AreIndependent()
    {
        var pushA = Push('a');
        var pushB = Push('b');
        var a = Split(1, Encode(pushA), contentHash: 1);
        var b = Split(1, Encode(pushB), contentHash: 2);

        _receiver.OnBegin("a", a.Begin);
        _receiver.OnBegin("b", b.Begin);
        for (var i = 0; i < Math.Max(a.Chunks.Length, b.Chunks.Length); i++)
        {
            if (i < a.Chunks.Length) Chunk("a", a.Chunks[i]);
            if (i < b.Chunks.Length) Chunk("b", b.Chunks[i]);
        }

        using (Assert.EnterMultipleScope())
        {
            Assert.That(_completed.Select(c => c.Host), Is.EquivalentTo(["a", "b"]));
            Assert.That(_completed.Single(c => c.Host == "a").Message.Deserialize<ClipboardPushMessage>(), Is.EqualTo(pushA));
            Assert.That(_completed.Single(c => c.Host == "b").Message.Deserialize<ClipboardPushMessage>(), Is.EqualTo(pushB));
        }
    }

    [Test]
    public void Drop_DropsOnlyThatHost()
    {
        var a = Split(1, Encode(Push('a')), contentHash: 1);
        var b = Split(1, Encode(Push('b')), contentHash: 2);

        _receiver.OnBegin("a", a.Begin);
        _receiver.OnBegin("b", b.Begin);
        _receiver.Drop("a");

        using (Assert.EnterMultipleScope())
        {
            Assert.That(_receiver.PendingFrom("a"), Is.Null);
            Assert.That(_receiver.PendingFrom("b"), Is.EqualTo(2));
        }
        foreach (var chunk in a.Chunks) Chunk("a", chunk);
        foreach (var chunk in b.Chunks) Chunk("b", chunk);
        using (Assert.EnterMultipleScope())
        {
            Assert.That(_completed.Select(c => c.Host), Is.EqualTo(["b"]));
            Assert.That(_failed, Is.Empty);
            Assert.That(_sync.SetClipboardCallCount, Is.Zero);
        }
    }

    [Test]
    public void DropAll_DropsEveryHost()
    {
        var a = Split(1, Encode(Push('a')), contentHash: 1);
        var b = Split(1, Encode(Push('b')), contentHash: 2);

        _receiver.OnBegin("a", a.Begin);
        _receiver.OnBegin("b", b.Begin);
        _receiver.DropAll();

        using (Assert.EnterMultipleScope())
        {
            Assert.That(_receiver.PendingFrom("a"), Is.Null);
            Assert.That(_receiver.PendingFrom("b"), Is.Null);
        }
        foreach (var chunk in a.Chunks) Chunk("a", chunk);
        foreach (var chunk in b.Chunks) Chunk("b", chunk);
        _clock.Advance(ClipboardStreamReceiver.Watchdog * 2);
        using (Assert.EnterMultipleScope())
        {
            Assert.That(_completed, Is.Empty);
            Assert.That(_failed, Is.Empty);
        }
    }

    [Test]
    public void OnBegin_SaysWhetherItAccepted()
    {
        using (Assert.EnterMultipleScope())
        {
            Assert.That(_receiver.OnBegin("a", Split(1, Encode(Push('a'))).Begin), Is.True);
            Assert.That(_receiver.OnBegin("c", Split(2, Encode(Push('c'))).Begin with { TotalBytes = 0 }), Is.False);
            Assert.That(_receiver.OnBegin("d", Split(3, Encode(Push('d')), inner: MessageKind.ClipboardPullResponse).Begin), Is.False);
        }
        _receiver.Dispose();
        Assert.That(_receiver.OnBegin("a", Split(4, Encode(Push('a'))).Begin), Is.False);
    }

    [Test]
    public void Supersede_DropsTheOtherHosts_AndHoldsThemOffWhileTheWinnerStreams()
    {
        var a = Split(1, Encode(Push('a')), contentHash: 1);
        var b = Split(2, Encode(Push('b')), contentHash: 2);
        _receiver.OnBegin("a", a.Begin);
        _receiver.OnBegin("b", b.Begin);

        var dropped = _receiver.Supersede("b");
        _receiver.OnBegin("b", b.Begin);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(dropped, Is.EqualTo([new DroppedClipboardStream("a", 1)]));
            Assert.That(_receiver.PendingFrom("a"), Is.Null);
            Assert.That(_receiver.PendingFrom("b"), Is.EqualTo(2));
            Assert.That(_receiver.HeldOffBy("a"), Is.EqualTo("b"));
            Assert.That(_receiver.HeldOffBy("A"), Is.EqualTo("b"), "host names compare case-insensitively");
            Assert.That(_receiver.HeldOffBy("b"), Is.Null, "the winner is never held off");
            Assert.That(_receiver.HeldOffBy("c"), Is.Null, "a host with nothing superseded is not held off");
            Assert.That(_failed, Is.Empty);
        }
    }

    [Test]
    public void HeldOff_ClearsOnceTheWinnersStreamCompletes()
    {
        var a = Split(1, Encode(Push('a')), contentHash: 1);
        var b = Split(2, Encode(Push('b')), contentHash: 2);
        _receiver.OnBegin("a", a.Begin);
        _receiver.Supersede("b");
        _receiver.OnBegin("b", b.Begin);

        foreach (var chunk in b.Chunks) Chunk("b", chunk);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(_completed.Select(c => c.Host), Is.EqualTo(["b"]));
            Assert.That(_receiver.HeldOffBy("a"), Is.Null);
        }
        // cleared for good: the winner beginning again without superseding holds no one off
        _receiver.OnBegin("b", Split(3, Encode(Push('b'))).Begin);
        Assert.That(_receiver.HeldOffBy("a"), Is.Null);
    }

    [Test]
    public void HeldOff_ClearsOnceTheWinnersStreamIsDropped()
    {
        _receiver.OnBegin("a", Split(1, Encode(Push('a'))).Begin);
        _receiver.Supersede("b");
        _receiver.OnBegin("b", Split(2, Encode(Push('b'))).Begin);
        Assert.That(_receiver.HeldOffBy("a"), Is.EqualTo("b"));

        _receiver.Drop("b");

        Assert.That(_receiver.HeldOffBy("a"), Is.Null);
    }

    [Test]
    public void HeldOff_ClearsOnceTheWinnersStreamTimesOut()
    {
        _receiver.OnBegin("a", Split(1, Encode(Push('a'))).Begin);
        _receiver.Supersede("b");
        _receiver.OnBegin("b", Split(2, Encode(Push('b'))).Begin);

        _clock.Advance(ClipboardStreamReceiver.Watchdog);

        Assert.That(_receiver.HeldOffBy("a"), Is.Null);
    }

    [Test]
    public void Supersede_WithoutAStreamFromTheWinner_HoldsNoOneOff()
    {
        _receiver.OnBegin("a", Split(1, Encode(Push('a'))).Begin);
        _receiver.OnBegin("c", Split(3, Encode(Push('c'))).Begin);

        // a whole push supersedes, and leaves nothing pending
        _receiver.Supersede("b");
        Assert.That(_receiver.HeldOffBy("a"), Is.Null);

        // a rejected Begin leaves nothing pending either
        _receiver.OnBegin("a", Split(4, Encode(Push('a'))).Begin);
        _receiver.Supersede("c");
        _receiver.OnBegin("c", Split(5, Encode(Push('c'))).Begin with { TotalBytes = 0 });
        Assert.That(_receiver.HeldOffBy("a"), Is.Null);
    }

    [Test]
    public void Supersede_ByANewWinner_KeepsTheHostsTheOldWinnerHeldOff()
    {
        _receiver.OnBegin("a", Split(1, Encode(Push('a'))).Begin);
        _receiver.Supersede("b");
        _receiver.OnBegin("b", Split(2, Encode(Push('b'))).Begin);

        var dropped = _receiver.Supersede("c");
        _receiver.OnBegin("c", Split(3, Encode(Push('c'))).Begin);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(dropped, Is.EqualTo([new DroppedClipboardStream("b", 2)]));
            Assert.That(_receiver.HeldOffBy("a"), Is.EqualTo("c"));
            Assert.That(_receiver.HeldOffBy("b"), Is.EqualTo("c"));
        }
    }

    [Test]
    public void Supersede_ByTheWinnerAgain_HoldsOffUntilItsNewestStreamEnds()
    {
        _receiver.OnBegin("a", Split(1, Encode(Push('a'))).Begin);
        _receiver.Supersede("b");
        _receiver.OnBegin("b", Split(2, Encode(Push('b'))).Begin);

        _receiver.Supersede("b");
        var newer = Split(3, Encode(Push('b')));
        _receiver.OnBegin("b", newer.Begin);
        Assert.That(_receiver.HeldOffBy("a"), Is.EqualTo("b"));

        foreach (var chunk in newer.Chunks) Chunk("b", chunk);
        Assert.That(_receiver.HeldOffBy("a"), Is.Null);
    }

    [Test]
    public void Supersede_AfterTheWinnersStreamEnded_StartsAfresh()
    {
        _receiver.OnBegin("a", Split(1, Encode(Push('a'))).Begin);
        _receiver.Supersede("b");
        _receiver.OnBegin("b", Split(2, Encode(Push('b'))).Begin);
        _receiver.Drop("b");

        _receiver.OnBegin("d", Split(4, Encode(Push('d'))).Begin);
        _receiver.Supersede("c");
        _receiver.OnBegin("c", Split(3, Encode(Push('c'))).Begin);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(_receiver.HeldOffBy("a"), Is.Null, "the ended winner's hold is gone");
            Assert.That(_receiver.HeldOffBy("d"), Is.EqualTo("c"));
        }
    }

    [Test]
    public void HostNames_CompareCaseInsensitively()
    {
        var stream = Split(1, Encode(Push('x')));

        _receiver.OnBegin("Alpha", stream.Begin);
        Assert.That(_receiver.PendingFrom("ALPHA"), Is.EqualTo(ContentHash));
        foreach (var chunk in stream.Chunks) Chunk("alpha", chunk);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(_completed, Has.Count.EqualTo(1));
            Assert.That(_receiver.PendingFrom("Alpha"), Is.Null);
        }
    }

    [Test]
    public void OnChunk_ReturnsNullUntilTheLastChunk_ThenTheMessage()
    {
        var push = Push('x');
        var stream = Split(1, Encode(push));

        _receiver.OnBegin("a", stream.Begin);
        foreach (var chunk in stream.Chunks[..^1])
            Assert.That(_receiver.OnChunk("a", chunk), Is.Null);
        var completed = _receiver.OnChunk("a", stream.Chunks[^1]);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(completed?.Kind, Is.EqualTo(MessageKind.ClipboardPush));
            Assert.That(completed?.Deserialize<ClipboardPushMessage>(), Is.EqualTo(push));
        }
    }

    [Test]
    public void OnChunk_ReturnsNullOnFailure()
    {
        var stream = Split(1, Encode(Push('x')), blobHash: 12345);

        _receiver.OnBegin("a", stream.Begin);
        var results = stream.Chunks.Select(chunk => _receiver.OnChunk("a", chunk)).ToList();

        using (Assert.EnterMultipleScope())
        {
            Assert.That(results, Is.All.Null);
            Assert.That(_failed, Is.EqualTo([new StreamRef("a", 1)]));
        }
    }

    [Test]
    public void Begin_ClearsThroughTheAction_NotTheSync()
    {
        _receiver.OnBegin("a", Split(1, Encode(Push('x'))).Begin);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(_clears, Is.EqualTo(1));
            Assert.That(_sync.ClearClipboardCallCount, Is.Zero);
        }
    }

    [Test]
    public void Retain_DropsHostsNotPresent_CaseInsensitively_WithoutEvents()
    {
        var a = Split(1, Encode(Push('a')), contentHash: 1);
        var b = Split(1, Encode(Push('b')), contentHash: 2);

        _receiver.OnBegin("a", a.Begin);
        _receiver.OnBegin("b", b.Begin);
        _receiver.Retain(new HashSet<string> { "B" });

        using (Assert.EnterMultipleScope())
        {
            Assert.That(_receiver.PendingFrom("a"), Is.Null);
            Assert.That(_receiver.PendingFrom("b"), Is.EqualTo(2));
            Assert.That(_failed, Is.Empty);
            Assert.That(_abandoned, Is.Empty);
        }
        foreach (var chunk in a.Chunks) Chunk("a", chunk);
        foreach (var chunk in b.Chunks) Chunk("b", chunk);
        _clock.Advance(ClipboardStreamReceiver.Watchdog * 2);
        using (Assert.EnterMultipleScope())
        {
            Assert.That(_completed.Select(c => c.Host), Is.EqualTo(["b"]));
            Assert.That(_failed, Is.Empty);
        }
    }

    private void Chunk(string host, ClipboardStreamChunkMessage chunk)
    {
        var completed = _receiver.OnChunk(host, chunk);
        if (completed != null) _completed.Add(new CompletedStream(host, completed));
    }

    private void Deliver(string host, StreamedClipboard stream)
    {
        _receiver.OnBegin(host, stream.Begin);
        foreach (var chunk in stream.Chunks) Chunk(host, chunk);
    }

    // failed once, raising the stream's id for the abort back to its sender
    private void AssertFailedOnce(string host, long id = 1)
    {
        using (Assert.EnterMultipleScope())
        {
            Assert.That(_failed.Select(f => f.Host), Is.EqualTo([host]));
            Assert.That(_failed.Select(f => f.Id), Is.EqualTo([id]), "the id the sender is Aborted with");
            Assert.That(_completed, Is.Empty);
            Assert.That(_abandoned, Is.Empty);
            Assert.That(_receiver.PendingFrom(host), Is.Null);
            AssertWarnedOnce(host);
        }
    }

    // a failure is logged once as a warning naming the host
    private void AssertWarnedOnce(string host) =>
        Assert.That(_log.Entries.Where(e => e.Level == LogLevel.Warning && e.Message.Contains($"from {host}", StringComparison.Ordinal)).ToList(),
            Has.Count.EqualTo(1));

    private static ClipboardPushMessage Push(char fill) => new(new string(fill, 450));

    private static byte[] Encode(ClipboardPushMessage push) => MessageSerializer.Encode(MessageKind.ClipboardPush, push);

    // a blob as a sender streams it: its Begin, then chunks of ChunkBytes
    private static StreamedClipboard Split(long id, byte[] blob, MessageKind inner = MessageKind.ClipboardPush, ulong contentHash = ContentHash,
        ulong? blobHash = null) => StreamedClipboard.Split(id, blob, ChunkBytes, inner, contentHash, blobHash);

    private sealed record CompletedStream(string Host, DecodedMessage Message);

    // a stream given up on or abandoned, by its host and id
    private sealed record StreamRef(string Host, long Id);
}

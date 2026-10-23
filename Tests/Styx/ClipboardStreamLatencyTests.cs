using System.IO.Hashing;
using Hydra.Relay;
using Tests.Setup;

namespace Tests.Styx;

/// <summary>
/// A large clipboard does not delay input, end to end: the real <c>ClipboardStreamSender</c> over a real relay client
/// through the in-memory Styx relay.
///
/// <para>Nothing here is timed. The stream's pace is a gate in front of the client: the chunks before one index pass,
/// that one parks until the test releases it, so the test decides exactly how much of the clipboard has gone out.</para>
/// </summary>
[TestFixture]
public class ClipboardStreamLatencyTests : StyxFixtureBase
{
    private const string Target = "receiver";

    // 4 MiB of image, which encodes to well over the threshold and about twenty chunks
    private static ClipboardPushMessage LargePush()
    {
        var image = new byte[4 * 1024 * 1024];
        new Random(12).NextBytes(image);
        return new ClipboardPushMessage("", ImagePng: image);
    }

    private static ClipboardStreamSender StreamOver(IRelaySender relay) =>
        new(relay, () => Target, TestLog.CreateLogger<ClipboardStreamSender>());

    private static bool IsInput(ReceivedMessage message) => message.Kind is MessageKind.KeyEvent or MessageKind.KeyEventBatch;

    /// <summary>
    /// <b>The reason the clipboard streams.</b> A keystroke sent while a 4 MiB push is part way out arrives before the
    /// rest of it, which as one <c>ClipboardPush</c> on the input lane it could not. And the stream is still whole.
    /// </summary>
    [Test]
    public async Task AKeystrokeIsNotQueuedBehindALargeClipboard()
    {
        await using var pair = await ConnectedPair(receiver: Target);
        var (sender, receiver) = pair;
        var relay = new PacedRelay(sender, parkAt: 2);
        await using var stream = StreamOver(relay);

        var push = LargePush();
        var blob = MessageSerializer.Encode(MessageKind.ClipboardPush, push);
        stream.Send(Target, MessageKind.ClipboardPush, push);
        var parkedOffset = await relay.Parked.WaitAsync(Bound);

        sender.SendKeyEvent([Target], TestMessages.KeyMessage(0));

        // read up to the keystroke while the third chunk is still parked, so it cannot be behind it
        var beforeKey = new List<ReceivedMessage>();
        while (await receiver.WaitForMessage("the keystroke (if it never comes, it is behind the clipboard)") is var message && !IsInput(message))
            beforeKey.Add(message);

        relay.Gate.Release();

        Assert.That(beforeKey.Select(m => m.Kind).FirstOrDefault(), Is.EqualTo(MessageKind.ClipboardStreamBegin), "the Begin leads");
        var begin = beforeKey[0].As<ClipboardStreamBeginMessage>();
        var early = beforeKey.Skip(1).ToList();
        Assert.That(early.Select(m => m.Kind), Is.All.EqualTo(MessageKind.ClipboardStreamChunk));
        var chunks = early.Select(m => m.As<ClipboardStreamChunkMessage>()).ToList();
        var received = chunks.Sum(c => c.Data.Length);
        while (received < begin.TotalBytes)
        {
            var rest = await receiver.WaitForMessage("the rest of the clipboard");
            Assert.That(rest.Kind, Is.EqualTo(MessageKind.ClipboardStreamChunk));
            chunks.Add(rest.As<ClipboardStreamChunkMessage>());
            received += chunks[^1].Data.Length;
        }

        var expectedOffsets = Enumerable.Range(0, chunks.Count).Select(i => i * ClipboardStreamSender.ChunkBytes);
        var reassembled = chunks.SelectMany(c => c.Data).ToArray();
        using (Assert.EnterMultipleScope())
        {
            Assert.That(chunks.Take(early.Count).Select(c => c.Offset), Is.All.LessThan(parkedOffset),
                "only the chunks already let through may come before the keystroke");
            Assert.That(chunks.Select(c => c.Offset), Is.EqualTo(expectedOffsets), "chunks arrive in offset order");
            Assert.That(chunks.Select(c => c.Id), Is.All.EqualTo(begin.Id));
            Assert.That(reassembled, Is.EqualTo(blob), "the chunks reassemble to the encoded push");
            Assert.That(XxHash64.HashToUInt64(reassembled), Is.EqualTo(begin.BlobHash));
        }
    }

    /// <summary>
    /// The Begin, on the input lane, comes before every chunk, on the bulk one, even with input queued ahead of it: the
    /// receiver drops a chunk for a stream it has not been told of.
    /// </summary>
    [Test]
    public async Task TheBeginPrecedesEveryChunkWhileInputIsBusy()
    {
        await using var pair = await ConnectedPair(receiver: Target);
        var (sender, receiver) = pair;
        await using var stream = StreamOver(sender);

        // queued all at once, NOT awaited one by one, for the reason RelayLaneTests.ABurstOfInputArrivesInOrder gives
        var sent = Enumerable.Range(0, 200).Select(i => sender.SendReliableAsync([Target], TestMessages.Key(i)).AsTask()).ToArray();
        stream.Send(Target, MessageKind.ClipboardPush, LargePush());

        var kinds = new List<MessageKind>();
        int? total = null;
        var received = 0;
        while (total is null || received < total)
        {
            var message = await receiver.WaitForMessage("the whole clipboard stream");
            if (message.Kind == MessageKind.ClipboardStreamBegin) total = message.As<ClipboardStreamBeginMessage>().TotalBytes;
            if (message.Kind == MessageKind.ClipboardStreamChunk) received += message.As<ClipboardStreamChunkMessage>().Data.Length;
            if (message.Kind is MessageKind.ClipboardStreamBegin or MessageKind.ClipboardStreamChunk) kinds.Add(message.Kind);
        }
        await Task.WhenAll(sent).WaitAsync(Bound);

        Assert.That(kinds.IndexOf(MessageKind.ClipboardStreamBegin), Is.Zero, "a chunk came before its Begin and would be dropped");
    }

    /// <summary>
    /// A newer clipboard to the same host aborts the stream it supersedes, and the abort reaches the receiver before the
    /// new clipboard. The one chunk already in flight may still follow the abort; nothing after it does.
    /// </summary>
    [Test]
    public async Task ANewerClipboardSupersedesAStreamPartWayOut()
    {
        await using var pair = await ConnectedPair(receiver: Target);
        var (sender, receiver) = pair;
        var relay = new PacedRelay(sender, parkAt: 4);
        await using var stream = StreamOver(relay);

        stream.Send(Target, MessageKind.ClipboardPush, LargePush());
        var parkedOffset = await relay.Parked.WaitAsync(Bound);

        stream.Send(Target, MessageKind.ClipboardPush, new ClipboardPushMessage("small"));
        relay.Gate.Release();
        await relay.ParkedForwarded.WaitAsync(Bound);
        // the in-flight chunk's invocation has returned, so the relay delivers the sentinel after it
        sender.Send([Target], TestMessages.Sentinel());

        var frames = await receiver.ReadUntilSentinel();
        var oldId = frames.First(f => f.Kind == MessageKind.ClipboardStreamBegin).As<ClipboardStreamBeginMessage>().Id;
        var abort = frames.FindIndex(f => f.Kind == MessageKind.ClipboardStreamAbort);
        var small = frames.FindIndex(f => f.Kind == MessageKind.ClipboardPush);
        Assert.That(abort, Is.Not.Negative, "the superseded stream was never aborted");
        var chunksAfterAbort = frames.Skip(abort + 1).Where(f => f.Kind == MessageKind.ClipboardStreamChunk)
            .Select(f => f.As<ClipboardStreamChunkMessage>()).Where(c => c.Id == oldId).Select(c => c.Offset).ToList();

        using (Assert.EnterMultipleScope())
        {
            Assert.That(frames[abort].As<ClipboardStreamAbortMessage>().Id, Is.EqualTo(oldId));
            Assert.That(small, Is.GreaterThan(abort), "the new clipboard must come after the abort of the old one");
            Assert.That(frames[small].As<ClipboardPushMessage>().Text, Is.EqualTo("small"));
            Assert.That(chunksAfterAbort, Is.SubsetOf([parkedOffset]), "the old stream went on after its abort");
        }
    }

    /// <summary>
    /// The real client, with each <c>ClipboardStreamChunk</c> from index <paramref name="parkAt"/> on held on
    /// <see cref="Gate"/> before it is passed on. <see cref="Parked"/> gives the offset of the first one held.
    /// </summary>
    private sealed class PacedRelay(IRelaySender inner, int parkAt) : NullRelaySender
    {
        private readonly TaskCompletionSource<int> _parked = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource _parkedForwarded = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private int _chunks;

        public Gate Gate { get; } = new(held: true);
        public Task<int> Parked => _parked.Task;
        // the parked chunk has been handed to the client and its send has finished
        public Task ParkedForwarded => _parkedForwarded.Task;

        public override bool IsConnected => inner.IsConnected;

        public override void Send(string[] targetHosts, byte[] payload) => inner.Send(targetHosts, payload);

        public override async ValueTask SendReliableAsync(string[] targetHosts, byte[] payload, CancellationToken cancel = default)
        {
            if (payload[0] != (byte)MessageKind.ClipboardStreamChunk)
            {
                await inner.SendReliableAsync(targetHosts, payload, cancel);
                return;
            }

            var offset = MessageSerializer.Decode(payload).Deserialize<ClipboardStreamChunkMessage>().Offset;
            var index = Interlocked.Increment(ref _chunks) - 1;
            if (index < parkAt)
            {
                await inner.SendReliableAsync(targetHosts, payload, cancel);
                return;
            }

            if (index == parkAt) _parked.TrySetResult(offset);
            await Gate.Passed(cancel);
            try { await inner.SendReliableAsync(targetHosts, payload, cancel); }
            finally
            {
                if (index == parkAt) _parkedForwarded.TrySetResult();
            }
        }
    }
}

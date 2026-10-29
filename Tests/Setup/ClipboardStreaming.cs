using System.Text.Json;
using Cathedral.Config;
using Cathedral.Extensions;
using Hydra.Platform;
using Hydra.Relay;
using Microsoft.Extensions.Logging.Abstractions;

namespace Tests.Setup;

// clipboard streams by hand, either way: what a test feeds a master's router (a FakeRelay) or a slave (a TestableSlaveRelay),
// and reads back from what each sent; imported with `using static`
public static class ClipboardStreaming
{
    // the master a TestableSlaveRelay hears from
    public const string Master = "master-pc";

    // a stream's content hash where the clipboard inside doesn't matter
    public const ulong ContentHash = 0xC0FFEE;

    // -- content --

    // well over the stream threshold, so it streams in several chunks
    public static string BigText(char fill) => new(fill, 1024 * 1024);

    public static byte[] BigImage(int seed, int mebibytes = 1)
    {
        var image = new byte[mebibytes * 1024 * 1024];
        new Random(seed).NextBytes(image);
        return image;
    }

    public static ulong TextHash(string text) => ClipboardUtils.ClipboardHash(new ClipboardSnapshot(text, null, null));

    public static ulong ImageHash(byte[] image) => ClipboardUtils.ClipboardHash(new ClipboardSnapshot(null, null, image));

    // the hash of the whole clipboard a push or pull answer carries, as its receiver takes it
    public static ulong CarriedHash(IClipboardPayload message) =>
        ClipboardUtils.ClipboardHash(ClipboardWire.From(message, NullLogger.Instance, "a test stream"));

    // how many chunks a blob streams in
    public static int ChunkCount(long bytes) => (int)((bytes + ClipboardStreamSender.ChunkBytes - 1) / ClipboardStreamSender.ChunkBytes);

    public static int ChunkCount(byte[] blob) => ChunkCount(blob.Length);

    // every OSD shown so far, taken off the queue
    public static async Task<List<string>> Shown(RecordingOsd osd)
    {
        List<string> shown = [];
        while (osd.Shown.Count > 0) shown.Add(await osd.Shown.Next("an OSD"));
        return shown;
    }

    // -- into the master --

    // a pull response as a slave streams it, its Begin naming the pull it answers and carrying the hash of the clipboard inside
    public static StreamedClipboard StreamedAnswer(long id, ClipboardPullResponseMessage answer, ulong? blobHash = null) =>
        StreamedClipboard.Split(id, MessageSerializer.Encode(MessageKind.ClipboardPullResponse, answer), ClipboardStreamSender.ChunkBytes,
            MessageKind.ClipboardPullResponse, CarriedHash(answer), blobHash, answer.PullId);

    public static Task Begin(FakeRelay relay, StreamedClipboard stream, string host = "remote") =>
        relay.FireMessageReceived(host, MessageKind.ClipboardStreamBegin, stream.Begin.ToSaneJson());

    public static Task Chunk(FakeRelay relay, ClipboardStreamChunkMessage chunk, string host = "remote") =>
        relay.FireMessageReceived(host, MessageKind.ClipboardStreamChunk, chunk.ToSaneJson());

    public static Task Abort(FakeRelay relay, long id, string host = "remote") =>
        relay.FireMessageReceived(host, MessageKind.ClipboardStreamAbort, new ClipboardStreamAbortMessage(id).ToSaneJson());

    // the chunks from from up to to, calling track after each
    public static async Task Chunks(FakeRelay relay, StreamedClipboard stream, int from = 0, string host = "remote", int? to = null,
        Action? track = null)
    {
        foreach (var chunk in stream.Chunks[from..(to ?? stream.Chunks.Length)])
        {
            await Chunk(relay, chunk, host);
            track?.Invoke();
        }
    }

    // -- out of the master --

    public static List<ClipboardHashMessage> Hashes(FakeRelay relay, string host) => relay.Sent.Of<ClipboardHashMessage>(MessageKind.ClipboardHash, host);

    // the latest pull, to host or to any
    public static ClipboardPullMessage LatestPullTo(FakeRelay relay, string? host = null) =>
        relay.Sent.Of<ClipboardPullMessage>(MessageKind.ClipboardPull, host).Last();

    public static int PullIdTo(FakeRelay relay, string? host = null) => LatestPullTo(relay, host).PullId;

    // the ids of the streams aborted to host, by a master's relay or a slave's
    public static List<long> AbortsTo(SentSignals sent, string host) =>
        [.. sent.Of<ClipboardStreamAbortMessage>(MessageKind.ClipboardStreamAbort, host).Select(a => a.Id)];

    // where in sent the first message of kind to host matching match is, or -1
    public static int IndexOf<T>(List<SentMessage> sent, MessageKind kind, string host, Func<T, bool> match) =>
        sent.FindIndex(s => s.Kind == kind && s.Targets.Contains(host) && match(JsonSerializer.Deserialize<T>(s.Json, SaneJson.Options)!));

    // the latest push streamed to host, once the begins'th Begin to it has gone and its last chunk with it
    public static async Task<ClipboardPushMessage> SentPush(FakeRelay relay, string host, int begins = 1) =>
        (await Streamed(relay.Sent, host, begins, MessageKind.ClipboardPush)).Deserialize<ClipboardPushMessage>();

    // -- into a slave --

    // a push as the master streams it, its Begin carrying contentHash
    public static StreamedClipboard StreamedPush(long id, ClipboardPushMessage push, ulong contentHash = ContentHash, ulong? blobHash = null) =>
        StreamedClipboard.Split(id, MessageSerializer.Encode(MessageKind.ClipboardPush, push), ClipboardStreamSender.ChunkBytes,
            MessageKind.ClipboardPush, contentHash, blobHash);

    // a push as the master streams it, its Begin carrying the hash of the clipboard inside
    public static StreamedClipboard HashedPush(long id, ClipboardPushMessage push) =>
        StreamedPush(id, push, CarriedHash(push));

    public static Task Begin(TestableSlaveRelay slave, StreamedClipboard stream, string host = Master) =>
        slave.SimulateReceive(host, MessageKind.ClipboardStreamBegin, stream.Begin.ToSaneJson());

    public static Task Chunk(TestableSlaveRelay slave, ClipboardStreamChunkMessage chunk, string host = Master) =>
        slave.SimulateReceive(host, MessageKind.ClipboardStreamChunk, chunk.ToSaneJson());

    public static Task Abort(TestableSlaveRelay slave, long id, string host = Master) =>
        slave.SimulateReceive(host, MessageKind.ClipboardStreamAbort, new ClipboardStreamAbortMessage(id).ToSaneJson());

    public static async Task Chunks(TestableSlaveRelay slave, StreamedClipboard stream, int from = 0, string host = Master)
    {
        foreach (var chunk in stream.Chunks.Skip(from)) await Chunk(slave, chunk, host);
    }

    // the master's pull, as the slave receives it
    public static Task Pull(TestableSlaveRelay slave, int pullId = 1, ulong? masterHash = null, string host = Master) =>
        slave.SimulateReceive(host, MessageKind.ClipboardPull, new ClipboardPullMessage(pullId, masterHash).ToSaneJson());

    // -- out of a slave --

    // the latest answer streamed to host, once the begins'th Begin to it has gone and its last chunk with it
    public static async Task<ClipboardPullResponseMessage> SentAnswer(TestableSlaveRelay slave, int begins = 1, string host = Master) =>
        (await Streamed(slave.Sent, host, begins, MessageKind.ClipboardPullResponse)).Deserialize<ClipboardPullResponseMessage>();

    // the latest stream to host, once the begins'th Begin to it has gone and its last chunk with it, decoded as kind
    private static async Task<DecodedMessage> Streamed(SentSignals sent, string host, int begins, MessageKind kind)
    {
        await sent.WaitFor(MessageKind.ClipboardStreamBegin, begins, host);
        var begin = sent.Of<ClipboardStreamBeginMessage>(MessageKind.ClipboardStreamBegin, host).Last();
        var count = ChunkCount(begin.TotalBytes);
        var others = sent.Of<ClipboardStreamChunkMessage>(MessageKind.ClipboardStreamChunk, host).Count(c => c.Id != begin.Id);
        await sent.WaitFor(MessageKind.ClipboardStreamChunk, others + count, host);
        var chunks = sent.Of<ClipboardStreamChunkMessage>(MessageKind.ClipboardStreamChunk, host).Where(c => c.Id == begin.Id);
        var message = MessageSerializer.Decode([.. chunks.SelectMany(c => c.Data)]);
        Assert.That(message.Kind, Is.EqualTo(kind));
        return message;
    }
}

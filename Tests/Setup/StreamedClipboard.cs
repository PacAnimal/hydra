using System.IO.Hashing;
using Hydra.Relay;

namespace Tests.Setup;

// a blob as a ClipboardStreamSender streams it: its Begin, then chunks of chunkBytes
public sealed record StreamedClipboard(ClipboardStreamBeginMessage Begin, ClipboardStreamChunkMessage[] Chunks)
{
    // pullId: the pull a streamed pull response answers, as the sender names it on the Begin
    public static StreamedClipboard Split(long id, byte[] blob, int chunkBytes, MessageKind inner, ulong contentHash, ulong? blobHash = null,
        int? pullId = null)
    {
        var begin = new ClipboardStreamBeginMessage(id, inner, blob.Length, blobHash ?? XxHash64.HashToUInt64(blob), contentHash, pullId);
        var chunks = blob.Chunk(chunkBytes).Select((data, i) => new ClipboardStreamChunkMessage(id, i * chunkBytes, data)).ToArray();
        return new StreamedClipboard(begin, chunks);
    }
}

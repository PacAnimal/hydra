using Hydra.Keyboard;
using Hydra.Relay;

namespace Tests.Setup;

/// <summary>Encoded relay payloads, each identified by a number so a test can check arrival order.</summary>
public static class TestMessages
{
    public static byte[] Move(int n) => Move(n, n);

    public static byte[] Move(int x, int y) => MessageSerializer.Encode(MessageKind.MouseMove, new MouseMoveMessage("", x, y));

    // one key event, identified by the letter it carries
    public static KeyEventMessage KeyMessage(int n, KeyEventType type = KeyEventType.KeyDown) =>
        new(type, KeyModifiers.None, (char)('a' + n % 26), null);

    /// <summary>A KeyEvent, which unlike a mouse move is never coalesced — so N calls are N queue entries.</summary>
    public static byte[] Key(int n, KeyEventType type = KeyEventType.KeyDown) =>
        MessageSerializer.Encode(MessageKind.KeyEvent, KeyMessage(n, type));

    // marks the end of what a test expects: Osd neither coalesces nor bundles, so it takes a frame of its own
    public const MessageKind SentinelKind = MessageKind.Osd;

    public static byte[] Sentinel() => MessageSerializer.Encode(SentinelKind, new OsdMessage("end-of-test"));

    public static byte[] Chunk(int n, int bytes = 8) =>
        MessageSerializer.Encode(MessageKind.FileTransferChunk, new FileTransferChunkMessage(n, new byte[bytes]));
}

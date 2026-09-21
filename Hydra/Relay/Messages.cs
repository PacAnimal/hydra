using System.Text;
using Cathedral.Config;
using Cathedral.Extensions;
using Hydra.Keyboard;
using Hydra.Mouse;
using Microsoft.Extensions.Logging;

namespace Hydra.Relay;

public enum MessageKind : byte
{
    MouseMove = 1,
    KeyEvent = 2,
    MouseButton = 3,
    MouseScroll = 4,
    EnterScreen = 5,
    LeaveScreen = 6,
    MasterConfig = 7,
    ScreenInfo = 8,
    SlaveLog = 9,
    MouseMoveDelta = 10,
    ScreensaverSync = 11,
    ClipboardPush = 12,         // master → slave: apply this clipboard (text/image/files)
    ClipboardPull = 13,         // master → slave: send me your clipboard
    ClipboardPullResponse = 14, // slave → master: here's my clipboard
    // 15, 16 reserved (formerly used; do not reuse — breaks wire compat with older clients)
    FileTransferRequest = 17,   // master → receiver: can you receive? (SourceHost = actual data sender if different)
    FileTransferStart = 26,     // data source → receiver: here's what's coming (FileNames + TotalBytes)
    FileTransferChunk = 18,     // data source → receiver: chunk of tar.gz data
    FileTransferDone = 19,      // data source → receiver: all data sent
    FileTransferAbort = 20,     // either → either: abort and clean up
    FileTransferAccepted = 25,  // receiver → master: destination validated, ready to receive
    FileSelectionQuery = 21,    // master → slave: what files are selected?
    FileSelectionResponse = 22, // slave → master: here are the selected files
    FileStreamRequest = 23,     // master → source slave: stream these files to target
    Osd = 24,                   // master → slave: display an on-screen notification
    FileTransferBusy = 27,      // slave → master: transfer already in progress, request refused
    ClipboardHash = 28,         // master → slave: here's my clipboard hash (on screen enter)
    ClipboardPullRequest = 29,  // slave → master: my hash differs, please push your clipboard
    LockScreen = 30,            // master → slave: lock the screen
    ActivityPing = 31,          // either direction: poke idle timer; master re-broadcasts to other slaves if syncScreensaver
    LatencyProbe = 32,          // diagnostics-only peer RTT probe; receiver echoes the opaque sequence
    LatencyProbeResponse = 33,  // diagnostics-only response; never enters the input path
    RemoteManagementRequest = 34,
    RemoteManagementResponse = 35,
}

/// <summary>
/// Which of the relay's two outbound lanes a message travels on.
///
/// <para><b>One queue used to carry everything, and a 256 KiB file chunk sat in front of every keystroke
/// behind it.</b> Hydra is a KVM: input latency is the product. A transfer and a keypress have no causal
/// relationship, so they have no reason to queue behind one another — but messages WITHIN a lane very much
/// do, which is why this is two ordered lanes rather than "send it all concurrently".</para>
///
/// <para><b>This is the SENDING half only.</b> The receiving side still hands each frame to
/// <c>RelayConnection.Receive</c> on SignalR's own serial dispatch, and a slave writes a chunk inline from
/// there into a pipe whose default pause threshold is 64 KiB — so on the machine being controlled, a chunk
/// can still delay the input behind it. Fixing that is a separate piece of work on the receive path; do not
/// read this type as having solved it.</para>
/// </summary>
public enum RelayLane : byte
{
    /// <summary>Input and control. Strictly ordered, and everything not named below is here.</summary>
    Input,

    /// <summary>The file-transfer stream, and only that. Strictly ordered within itself.</summary>
    Bulk
}

public static class MessageLane
{
    /// <summary>
    /// The lane a kind travels on.
    ///
    /// <para><b>Only the three STREAM kinds are bulk, and they must all three be together.</b>
    /// <c>FileTransferStart</c> creates the receiver's extractor, the chunks feed it, and
    /// <c>FileTransferDone</c> finalises and checks the hash — so a Start that lost its race with chunk 0
    /// would be dropped by <c>HandleFileTransferChunkAsync</c>'s <c>receiver?.Extractor == null</c> guard
    /// without a word, and a Done that overtook the last chunk would truncate the transfer. Splitting them
    /// across lanes would introduce exactly the defect the split exists to avoid.</para>
    ///
    /// <para><b><c>FileTransferAbort</c> is deliberately NOT bulk.</b> It means stop, so it wants to overtake
    /// the chunks still queued rather than wait behind them, and a chunk arriving after it normally hits the
    /// null-extractor guard and is discarded — the outcome an abort asks for. NOT unconditionally, though:
    /// nothing on the wire carries a transfer id, and the receiver is keyed on source host alone, so a
    /// stale chunk that arrives after a NEW transfer has been negotiated from the same host is accepted into
    /// it. That needs the bulk lane still stalled across a full negotiation round trip, and it surfaces as a
    /// failed integrity check rather than a bad file, because the hash is taken over arrival order. A
    /// transfer id on the four stream kinds would close it properly.</para>
    ///
    /// <para>The negotiation kinds (Request/Accepted/Busy, the FileSelection pair, FileStreamRequest) are
    /// control too: each is a round trip that completes before any stream starts, so they are ordered by the
    /// reply they wait for and not by the lane.</para>
    ///
    /// <para>Anything unrecognised is <see cref="RelayLane.Input"/>, which is the ordered lane and therefore
    /// the safe direction to be wrong in. <c>EveryMessageKindIsDeliberatelyAssignedToALane</c> makes a new
    /// kind fail the build's tests until somebody has actually decided.</para>
    /// </summary>
    public static RelayLane Of(MessageKind kind) => kind switch
    {
        MessageKind.FileTransferStart or MessageKind.FileTransferChunk or MessageKind.FileTransferDone => RelayLane.Bulk,
        _ => RelayLane.Input
    };

    /// <summary>
    /// The lane an encoded payload travels on — the wire format is <c>[1 byte kind][json]</c>, so the first
    /// byte is the whole question. An empty payload is Input, which is where anything unclassifiable goes.
    /// </summary>
    public static RelayLane Of(ReadOnlySpan<byte> payload) =>
        payload.Length == 0 ? RelayLane.Input : Of((MessageKind)payload[0]);
}

public record MouseMoveMessage(string Screen, int X, int Y);
public record MouseMoveDeltaMessage(int Dx, int Dy);
public record ScreenInfoEntry(string Name, int X, int Y, int Width, int Height, decimal MouseScale, decimal? RelativeMouseScale = null);

// ReSharper disable once InconsistentNaming
public enum PeerPlatform : byte { Unknown = 0, Linux = 1, MacOS = 2, Windows = 3 }

public record ScreenInfoMessage(List<ScreenInfoEntry> Screens, PeerPlatform? Platform = null);
public record MasterConfigMessage(LogLevel? LogLevel);
public record SlaveLogMessage(int Level, string Category, string Message, string? Exception);
// IsRepeat marks an OS auto-repeat the master re-resolved (with live modifier/dead-key state) and forwarded;
// the slave injects it without tracking a new held key. UnicodeKeyRepeat is the master's per-keypress repeat
// preference: when set, Mac slaves inject repeated characters via keycode-less unicode (avoiding the
// press-and-hold accent popup) rather than re-pressing the physical key. travelling per-keypress lets a
// shared slave honour each master's own preference.
public record KeyEventMessage(KeyEventType Type, KeyModifiers Modifiers, char? Character, SpecialKey? Key, bool IsRepeat = false, bool UnicodeKeyRepeat = true);
public record MouseButtonMessage(MouseButton Button, bool IsPressed);
public record MouseScrollMessage(short XDelta, short YDelta);
public record EnterScreenMessage(string Screen, int X, int Y, int Width, int Height);
public record ScreensaverSyncMessage(bool Active);
public record LeaveScreenMessage;
public record ClipboardPullMessage(ulong? MasterHash = null);
public record ClipboardPushMessage(string Text, string? PrimaryText = null, byte[]? ImagePng = null, string? Html = null, byte[]? Rtf = null);
public record ClipboardPullResponseMessage(string? Text, string? PrimaryText = null, byte[]? ImagePng = null, bool? Unchanged = null, string? Html = null, byte[]? Rtf = null);
public record ClipboardHashMessage(ulong Hash);
public record ClipboardPullRequestMessage;
public record LockScreenMessage(long MillisecondsSinceLastInput);

public record FileTransferRequestMessage(string? SourceHost = null);
public record FileTransferStartMessage(string[] FileNames, long TotalBytes);
public record FileTransferChunkMessage(int Sequence, byte[] Data);
public record FileTransferDoneMessage(long TotalBytesSent, byte[] Sha256);
public record FileTransferAbortMessage(string Reason);
public record FileTransferAcceptedMessage;
public record FileSelectionQueryMessage;
public record FileSelectionResponseMessage(string[]? Paths, string? NotFocusedMessage = null);
public record FileStreamRequestMessage(string[] Paths, string TargetHost);
public record OsdMessage(string Text);
public record FileTransferBusyMessage;
public record ActivityPingMessage;
public record LatencyProbeMessage(long Sequence);
public record LatencyProbeResponseMessage(long Sequence);

public static class MessageSerializer
{
    // wire format: [1 byte kind][utf-8 json]
    public static byte[] Encode<T>(MessageKind kind, T message)
    {
        var json = message.ToSaneJsonBytes(SaneJson.CompactOptions);
        var result = new byte[1 + json.Length];
        result[0] = (byte)kind;
        json.CopyTo(result, 1);
        return result;
    }

    public static DecodedMessage Decode(byte[] payload)
    {
        if (payload.Length == 0) throw new ArgumentException("Empty payload", nameof(payload));
        var kind = (MessageKind)payload[0];
        return new DecodedMessage(kind, payload.AsMemory(1));
    }
}

public record DecodedMessage(MessageKind Kind, ReadOnlyMemory<byte> Bytes)
{
    // lazy string conversion — only used in tests and low-frequency paths
    public string Json => Encoding.UTF8.GetString(Bytes.Span);
    public T Deserialize<T>() => Bytes.FromSaneJson<T>()!;
}

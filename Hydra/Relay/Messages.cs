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
    // 15, 16 unused
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
    KeyEventBatch = 36,         // master → slave: several KeyEvents in one frame, applied in order
}

/// <summary>
/// Which of the relay's two outbound lanes a message travels on: each is ordered, with no order between them, so a
/// file chunk never queues in front of a keystroke. Sending only; a slave's receive path still handles frames in turn.
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
    /// <para>The three stream kinds stay together on bulk: a Start overtaken by chunk 0 is dropped by the null-extractor
    /// guard, and a Done that overtakes the last chunk truncates the transfer.</para>
    /// <para>Abort is input on purpose, to overtake the chunks it cancels. Nothing on the wire carries a transfer id, so a
    /// stale chunk arriving after a new transfer from the same host joins it, and fails its hash check.</para>
    /// <para>Everything else, unknown kinds included, is input: the ordered lane is the safe one to be wrong in.</para>
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

public enum PeerPlatform : byte { Unknown = 0, Linux = 1, MacOs = 2, Windows = 3 }

public static class PeerPlatforms
{
    public static readonly PeerPlatform Local =
        OperatingSystem.IsMacOS() ? PeerPlatform.MacOs :
        OperatingSystem.IsWindows() ? PeerPlatform.Windows :
        OperatingSystem.IsLinux() ? PeerPlatform.Linux :
        PeerPlatform.Unknown;

    // the one place a platform becomes text a person reads
    public static string DisplayName(this PeerPlatform platform) => platform switch
    {
        PeerPlatform.Linux => "Linux",
        PeerPlatform.MacOs => "macOS",
        PeerPlatform.Windows => "Windows",
        _ => "unknown",
    };
}

public record ScreenInfoMessage(List<ScreenInfoEntry> Screens, PeerPlatform Platform);
public record MasterConfigMessage(LogLevel? LogLevel);
public record SlaveLogMessage(int Level, string Category, string Message, string? Exception);
// IsRepeat marks an OS auto-repeat the master re-resolved (with live modifier/dead-key state) and forwarded;
// the slave injects it without tracking a new held key.
public record KeyEventMessage(KeyEventType Type, KeyModifiers Modifiers, char? Character, SpecialKey? Key, bool IsRepeat = false);

/// <summary>
/// Several key events in one frame, to be applied IN ORDER.
///
/// <para><b>Bundled, never merged.</b> A mouse move supersedes the one before it, so movement coalesces
/// into a single position and the intermediate ones are discarded on purpose. Nothing about a key event is
/// superseded: a KeyDown and the KeyUp that follows are two facts, and losing either leaves a key held down
/// on somebody's machine. So this carries every event it was given, in the order it was given them.</para>
///
/// <para>What it buys is frames. The input lane sends a key event as soon as the one before it is written, so
/// events only pile up while the connection to the relay is backed up; then one frame carries them all instead of
/// one frame each.</para>
/// </summary>
public record KeyEventBatchMessage(KeyEventMessage[] Events);
public record MouseButtonMessage(MouseButton Button, bool IsPressed);
public record MouseScrollMessage(short XDelta, short YDelta);
public record EnterScreenMessage(string Screen, int X, int Y, int Width, int Height);
public record ScreensaverSyncMessage(bool Active);
public record LeaveScreenMessage;
public record ClipboardPullMessage(int PullId, ulong? MasterHash = null, ulong? MasterPrimaryHash = null);
// the selection a clipboard message carries; PrimaryIsText means it repeats the clipboard's text
public interface IClipboardSelection
{
    string? PrimaryText { get; }
    bool? PrimaryIsText { get; }
}

// a clipboard as both a push and a full pull answer carry it
public interface IClipboardPayload : IClipboardSelection
{
    string? Text { get; }
    byte[]? ImagePng { get; }
    string? Html { get; }
    byte[]? Rtf { get; }
}

public record ClipboardPushMessage(string Text, string? PrimaryText = null, byte[]? ImagePng = null, string? Html = null, byte[]? Rtf = null, bool? PrimaryIsText = null)
    : IClipboardPayload;
public record ClipboardPullResponseMessage(int PullId, string? Text, string? PrimaryText = null, byte[]? ImagePng = null, bool? Unchanged = null, string? Html = null,
    byte[]? Rtf = null, bool? PrimaryIsText = null) : IClipboardPayload;
// see ClipboardWire; a null Hash offers no clipboard, only the selection
public record ClipboardHashMessage(ulong? Hash, string? PrimaryText = null, bool? PrimaryIsText = null, ulong? PrimaryHash = null) : IClipboardSelection;
public record ClipboardPullRequestMessage;
public record LockScreenMessage(long MillisecondsSinceLastInput);

public record FileTransferRequestMessage(string? SourceHost = null);
public record FileTransferStartMessage(string[] FileNames, long TotalBytes);
public record FileTransferChunkMessage(int Sequence, byte[] Data);
public record FileTransferDoneMessage(long TotalBytesSent, byte[] Sha256);
public record FileTransferAbortMessage(string Reason);
public record FileTransferAcceptedMessage;
public record FileSelectionQueryMessage;
// Message is shown to the user in place of a copy, when there is none
public record FileSelectionResponseMessage(string[]? Paths, string? Message = null);
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
    public T Deserialize<T>() => Bytes.DecodeBody<T>()!;
}

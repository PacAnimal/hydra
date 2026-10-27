using System.Buffers;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
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
    ClipboardStreamBegin = 37,  // either → either: a large clipboard message follows in chunks; empty the clipboard until it lands
    ClipboardStreamChunk = 38,  // either → either: part of a streamed clipboard message
    ClipboardStreamAbort = 39,  // either → either: stop that stream
    ClipboardPushNone = 40,     // master → slave: no push follows your pull request
}

public static class MessageKinds
{
    // every kind of the clipboard handshake and its streams, either direction
    public static bool IsClipboard(MessageKind kind) => kind is MessageKind.ClipboardHash or MessageKind.ClipboardPush or MessageKind.ClipboardPull
        or MessageKind.ClipboardPullRequest or MessageKind.ClipboardPullResponse or MessageKind.ClipboardPushNone or MessageKind.ClipboardStreamBegin
        or MessageKind.ClipboardStreamChunk or MessageKind.ClipboardStreamAbort;
}

/// <summary>
/// Which of the relay's two outbound lanes a message travels on: each is ordered, with no order between them, so a
/// file chunk never queues in front of a keystroke. Sending only; a slave's receive path still handles frames in turn.
/// </summary>
public enum RelayLane : byte
{
    /// <summary>Input and control. Strictly ordered, and everything not named below is here.</summary>
    Input,

    /// <summary>The file-transfer stream and clipboard stream chunks, and only those. Strictly ordered within itself.</summary>
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
    /// <para>A clipboard stream puts only its chunks on bulk. Begin is input so the receiver's clear overtakes later
    /// keystrokes, and Abort to overtake the chunks it cancels. The sender sends Begin reliably before chunk 0, and chunks
    /// carry the stream Id, so a chunk cannot join the wrong stream.</para>
    /// <para>Everything else, unknown kinds included, is input: the ordered lane is the safe one to be wrong in.</para>
    /// </summary>
    public static RelayLane Of(MessageKind kind) => kind switch
    {
        MessageKind.FileTransferStart or MessageKind.FileTransferChunk or MessageKind.FileTransferDone
            or MessageKind.ClipboardStreamChunk => RelayLane.Bulk,
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

// SharingOff: the slave's clipboardSync is Off, so its master sends it no clipboard or file transfer
public record ScreenInfoMessage(List<ScreenInfoEntry> Screens, PeerPlatform Platform, bool SharingOff = false);
public record MasterConfigMessage(LogLevel? LogLevel);
public record SlaveLogMessage(int Level, string Category, string Message, string? Exception);
// IsRepeat marks an OS auto-repeat the master re-resolved (with live modifier/dead-key state) and forwarded, only for a
// key whose down it forwarded; the slave injects it as a repeat of that held key.
// Chorded marks the release of a modifier a hotkey was pressed under on the master, which swallowed that key: the
// release is no bare tap, so the slave opens no Start menu, overview or menu bar for it
public record KeyEventMessage(KeyEventType Type, KeyModifiers Modifiers, char? Character, SpecialKey? Key, bool IsRepeat = false, bool? Chorded = null);

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
// Focus: bring forward the window last worked in on that screen, as the monitor switch hotkey does on arrival
public record EnterScreenMessage(string Screen, int X, int Y, int Width, int Height, bool Focus = false);
public record ScreensaverSyncMessage(bool Active);
public record LeaveScreenMessage;
// ResendFrom: the first pull to this host whose answer the master did not take, so a clipboard synced by the full answer
// to it or a later one is answered again, never Unchanged as synced
public record ClipboardPullMessage(int PullId, ulong? MasterHash = null, ulong? MasterPrimaryHash = null, int? ResendFrom = null);
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
    // a password manager's copy, written with its markers on every host
    bool? Concealed { get; }
}

public record ClipboardPushMessage(
    string Text, string? PrimaryText = null, byte[]? ImagePng = null, string? Html = null, byte[]? Rtf = null, bool? PrimaryIsText = null,
    bool? Concealed = null) : IClipboardPayload;
// Cleared: the concealed copy this host last answered with was emptied here since (its password manager cleared it);
// Resends: a resend of the copy first answered to that older pull, which ranks it as that pull's answer
public record ClipboardPullResponseMessage(
    int PullId, string? Text, string? PrimaryText = null, byte[]? ImagePng = null, bool? Unchanged = null, string? Html = null,
    byte[]? Rtf = null, bool? PrimaryIsText = null, bool? Concealed = null, bool? Cleared = null, int? Resends = null) : IClipboardPayload;
// see ClipboardWire; a null Hash offers no clipboard, only the selection; Pending means a newer clipboard is on its way:
// empty yours and wait for the next hash; Cleared means the master's concealed copy was emptied: clear yours if it is
// still Hydra's write of that copy; Visit is the master's visit to this host it was offered on, echoed by the pull request;
// ResendFrom: as a pull's, the master never took this host's answer from that pull on, so a clipboard synced by it is kept
public record ClipboardHashMessage(
    ulong? Hash, string? PrimaryText = null, bool? PrimaryIsText = null, ulong? PrimaryHash = null, bool? Pending = null,
    bool? Cleared = null, int? Visit = null, int? ResendFrom = null) : IClipboardSelection;
// visit: the hash's Visit
public record ClipboardPullRequestMessage(int? Visit);
// the pull request for visit gets no push: the master's clipboard is empty or unreadable, or the visit is outdated
public record ClipboardPushNoneMessage(int? Visit);
// Inner is the streamed message's kind (ClipboardPush or ClipboardPullResponse), BlobHash xxhash64 of its encoded bytes,
// ContentHash the clipboard hash it carries, PullId the pull a streamed pull response answers, Resends its Resends
public record ClipboardStreamBeginMessage(long Id, MessageKind Inner, int TotalBytes, ulong BlobHash, ulong ContentHash, int? PullId = null,
    int? Resends = null);
public record ClipboardStreamChunkMessage(long Id, int Offset, byte[] Data);
// failed: the sender gave up on it (a send failed, or dormancy), which the receiver shows; else it was superseded
public record ClipboardStreamAbortMessage(long Id, bool? Failed = null);
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
    // relaxed escaping keeps non-ascii and markup as utf-8: the payload is aes-gcm encrypted end to end, never embedded in html
    private static readonly JsonSerializerOptions EncodeOptions = new(SaneJson.CompactOptions) { Encoder = RelaxedUtf8Encoder.Instance };

    // wire format: [1 byte kind][utf-8 json]
    public static byte[] Encode<T>(MessageKind kind, T message)
    {
        var json = message.ToSaneJsonBytes(EncodeOptions);
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

    // UnsafeRelaxedJsonEscaping, but leaving a valid surrogate pair (an emoji) as utf-8 rather than two \u escapes
    private sealed class RelaxedUtf8Encoder : JavaScriptEncoder
    {
        public static readonly RelaxedUtf8Encoder Instance = new();
        private static readonly JavaScriptEncoder Relaxed = UnsafeRelaxedJsonEscaping;

        public override int MaxOutputCharactersPerInputCharacter => Relaxed.MaxOutputCharactersPerInputCharacter;
        public override bool WillEncode(int unicodeScalar) => unicodeScalar <= char.MaxValue && Relaxed.WillEncode(unicodeScalar);

        public override unsafe bool TryEncodeUnicodeScalar(int unicodeScalar, char* buffer, int bufferLength, out int numberOfCharactersWritten) =>
            Relaxed.TryEncodeUnicodeScalar(unicodeScalar, buffer, bufferLength, out numberOfCharactersWritten);

        public override unsafe int FindFirstCharacterToEncode(char* text, int textLength)
        {
            var start = 0;
            while (true)
            {
                var found = Relaxed.FindFirstCharacterToEncode(text + start, textLength - start);
                if (found < 0) return -1;
                var at = start + found;
                if (at + 1 >= textLength || !char.IsSurrogatePair(text[at], text[at + 1])) return at;
                start = at + 2;
            }
        }

        public override int FindFirstCharacterToEncodeUtf8(ReadOnlySpan<byte> utf8Text)
        {
            var start = 0;
            while (true)
            {
                var found = Relaxed.FindFirstCharacterToEncodeUtf8(utf8Text[start..]);
                if (found < 0) return -1;
                var at = start + found;
                if (Rune.DecodeFromUtf8(utf8Text[at..], out var rune, out var consumed) != OperationStatus.Done || rune.IsBmp) return at;
                start = at + consumed;
            }
        }
    }
}

public record DecodedMessage(MessageKind Kind, ReadOnlyMemory<byte> Bytes)
{
    // lazy string conversion — only used in tests and low-frequency paths
    public string Json => Encoding.UTF8.GetString(Bytes.Span);
    public T Deserialize<T>() => Bytes.DecodeBody<T>()!;
}

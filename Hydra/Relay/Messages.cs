using System.Collections.Frozen;
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
    KeyEventBatch = 36,         // master → slave: several KeyEvents in one frame, applied in order
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

/// <param name="Capabilities">
/// What this peer can do, by NAME — see <see cref="PeerCapabilities"/>. Null or empty is what every build
/// before a given capability sends, and is read as "none of them", because the failure mode of guessing
/// wrong is silent: an unrecognised <see cref="MessageKind"/> reaches <c>RelayConnection.OnReceive</c>'s
/// base and is discarded without a word.
///
/// <para><b>STRINGS, not <see cref="PeerCapability"/> values, and that is load-bearing for two reasons.</b>
/// Cathedral's enum converter THROWS on a name or number it does not know, so an array of enums would make
/// a newer peer's unknown capability fail the whole <c>ScreenInfoMessage</c> — costing that peer's SCREENS,
/// which is far worse than not knowing about one feature. Names let a master ignore what it has never heard
/// of.</para>
///
/// <para>And names carry no NUMBERING to keep. Nothing on the wire depends on a capability's ordinal, so
/// members may be added, reordered or deleted freely and no hole has to be reserved for one that is gone.
/// Compare <see cref="MessageKind"/>, which is numbered and therefore carries "15, 16 reserved (formerly
/// used; do not reuse)" for ever. Do not "tidy" this into an enum array later: both reasons would be lost.</para>
/// </param>
public record ScreenInfoMessage(List<ScreenInfoEntry> Screens, PeerPlatform? Platform = null, string?[]? Capabilities = null);

/// <summary>
/// Something a peer can do that its peers must not assume.
///
/// <para>A capability exists when doing the thing at a peer that cannot would FAIL SILENTLY. That is the
/// bar: anything a peer would merely refuse, or answer with an error, needs no entry here.</para>
///
/// <para><b>These travel by NAME, so their numbering means nothing.</b> Add, reorder or delete members as
/// you like; there are no reserved values and no holes to preserve — see
/// <c>ScreenInfoMessage.Capabilities</c> for why the wire is names and must stay names.</para>
/// </summary>
public enum PeerCapability
{
    /// <summary>
    /// Applies <see cref="MessageKind.KeyEventBatch"/>. A peer without this is sent one key event per
    /// frame; sending it a batch would lose every key in it in silence.
    ///
    /// <para><b>REMOVE AFTER 2026-10-30</b> — the MEMBER and every check of it, not the mechanism. After
    /// that date every build applies a batch and asking is debt. See <see cref="PeerCapabilities.Mine"/>
    /// for the full deletion list.</para>
    /// </summary>
    KeyEventBatch
}

/// <summary>
/// What this build advertises, and how a peer's advertisement is read back.
///
/// <para>One place, so the two halves cannot drift: the names a slave sends are the names a master parses.</para>
/// </summary>
public static class PeerCapabilities
{
    /// <summary>
    /// Everything this build can do. A slave sends these on its <c>ScreenInfo</c>.
    ///
    /// <para><b>REMOVE AFTER 2026-10-30:</b> take <see cref="PeerCapability.KeyEventBatch"/> out of here and
    /// out of the enum, then delete <c>IWorldState.SetPeerCapabilities</c>'s only consumer —
    /// <c>RelayConnection.EveryTargetSupports</c> and the <c>&amp;&amp;</c> in <c>Send</c> — and
    /// <c>KeyBundleTests.APeerThatNeverAdvertisedSupportIsNeverSentABatch</c>, which is the one test about
    /// asking rather than about bundling. The mechanism itself stays for whatever needs it next.</para>
    /// </summary>
    public static readonly PeerCapability[] Mine = [PeerCapability.KeyEventBatch];

    /// <summary>The names to put on the wire for this build.</summary>
    public static string[] Advertise() => [.. Mine.Select(c => c.ToString())];

    /// <summary>
    /// The declared names, and nothing else.
    ///
    /// <para><b>Matched against this rather than parsed with <c>Enum.TryParse</c>, which reads far more than
    /// names.</b> It takes "0" as the member sitting at zero — so a peer sending a digit would be taken to
    /// claim whatever that happens to be today, which is precisely the numbering dependency this design
    /// exists to avoid — and it takes "A,B" as a bitwise OR. <c>Enum.IsDefined</c> closes neither: "0" is
    /// defined, and an OR of two members can land on a defined value too. A lookup cannot do either.</para>
    /// </summary>
    private static readonly FrozenDictionary<string, PeerCapability> ByName =
        Enum.GetValues<PeerCapability>().ToFrozenDictionary(c => c.ToString(), c => c, StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// The capabilities a peer advertised, ignoring any name this build does not know.
    ///
    /// <para><b>Ignoring is the whole point.</b> A name we do not recognise comes from a NEWER peer
    /// advertising something this build has never heard of — which is normal, and must cost nothing. Parsing
    /// strictly would throw away the message it arrived on.</para>
    ///
    /// <para>The ELEMENT is nullable because this array is deserialised from a peer's message: JSON
    /// <c>["KeyEventBatch", null]</c> is valid and lands a null in it, whatever the type says. The guard
    /// below is real, and annotating it away would make it look dead.</para>
    /// </summary>
    public static IReadOnlySet<PeerCapability> Parse(string?[]? names)
    {
        if (names is not { Length: > 0 }) return FrozenSet<PeerCapability>.Empty;

        var known = new HashSet<PeerCapability>();
        foreach (var name in names)
            if (name != null && ByName.TryGetValue(name, out var capability))
                known.Add(capability);

        return known;
    }
}
public record MasterConfigMessage(LogLevel? LogLevel);
public record SlaveLogMessage(int Level, string Category, string Message, string? Exception);
// IsRepeat marks an OS auto-repeat the master re-resolved (with live modifier/dead-key state) and forwarded;
// the slave injects it without tracking a new held key. UnicodeKeyRepeat is the master's per-keypress repeat
// preference: when set, Mac slaves inject repeated characters via keycode-less unicode (avoiding the
// press-and-hold accent popup) rather than re-pressing the physical key. travelling per-keypress lets a
// shared slave honour each master's own preference.
public record KeyEventMessage(KeyEventType Type, KeyModifiers Modifiers, char? Character, SpecialKey? Key, bool IsRepeat = false, bool UnicodeKeyRepeat = true);

/// <summary>
/// Several key events in one frame, to be applied IN ORDER.
///
/// <para><b>Bundled, never merged.</b> A mouse move supersedes the one before it, so movement coalesces
/// into a single position and the intermediate ones are discarded on purpose. Nothing about a key event is
/// superseded: a KeyDown and the KeyUp that follows are two facts, and losing either leaves a key held down
/// on somebody's machine. So this carries every event it was given, in the order it was given them.</para>
///
/// <para>What it buys is round trips. <c>Send</c> on the relay proxy is <c>InvokeCoreAsync</c>, which does
/// not complete until the hub method returns — so a lane sends one message at a time and pays a full
/// relay round trip for each. Type fast enough, or hold a key down, and a KeyDown and its KeyUp are both
/// waiting by the time the first goes out; one frame delivers both for the price of one trip.</para>
///
/// <para><b>Only to a peer that has said it understands this.</b> An unrecognised kind reaches
/// <c>RelayConnection.OnReceive</c>'s base, which does nothing at all — so sending one of these to a slave
/// that predates it would discard every key in it, in silence. See <see cref="PeerCapability.KeyEventBatch"/>.</para>
/// </summary>
public record KeyEventBatchMessage(KeyEventMessage[] Events);
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

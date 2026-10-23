using Hydra.Platform;
using Hydra.Relay;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;

namespace Tests.Setup;

public enum ClipboardOs { Linux, MacOs, Windows }

// An OS clipboard as Hydra's platform code sees it. What Hydra wrote reads back as an echo while it still holds the
// clipboard. X11 has a PRIMARY selection; the others keep the real CopiedPrimary, versioned by a change counter as the
// OS keeps one.
public sealed class SimulatedClipboard : IClipboardSync
{
    private static readonly NullLogger Log = NullLogger.Instance;

    private readonly ClipboardOs _os;
    private readonly CopiedPrimary _copied;
    private readonly ClipboardSlave _asSlave;
    private readonly ClipboardMaster _asMaster;
    // the streams this host takes as a slave and as a master; their watchdog never fires on a clock nobody steps
    private readonly ClipboardStreamReceiver _fromMaster;
    private readonly ClipboardStreamReceiver _fromSlave;
    private long _version;
    private bool _owned;
    private string? _x11Primary;
    private long _streamIds;

    public SimulatedClipboard(ClipboardOs os)
    {
        _os = os;
        _copied = new CopiedPrimary(() => _version, ReadTextCopy, Log);
        var clock = new FakeTimeProvider();
        // as SlaveRelayConnection builds them: a stream pending from the master counts as applied
        _asSlave = new ClipboardSlave(this, Log, PendingFromMaster);
        _fromMaster = new ClipboardStreamReceiver(this, (host, hash) => _asSlave.Begun(host, hash), _asSlave.Rejected, clock, Log);
        _asMaster = new ClipboardMaster(this, Log);
        _fromSlave = new ClipboardStreamReceiver(this, (host, _) => _asMaster.Begun(host), _asMaster.Rejected, clock, Log,
            MessageKind.ClipboardPullResponse);
        _fromMaster.Abandoned += (_, _) => AbandonedStreams++;
        _fromSlave.Abandoned += (_, _) => AbandonedStreams++;
    }

    // what is on the clipboard, a clear's placeholder reading as nothing; ReadAll reads it as it stands
    public string? Text => _text == ClipboardSnapshot.PlaceholderText ? null : _text;
    private string? _text;
    public byte[]? ImagePng { get; private set; }
    public string? Primary => GetPrimaryText();
    public int ClipboardWrites { get; private set; }

    // the incoming streams this host gave up on, because the clipboard was no longer Hydra's
    public int AbandonedStreams { get; private set; }

    // what a paste would get right now, echo or not
    public string? Paste() => Text;

    public void Copy(string text) => Take(text, null);

    // a password manager's copy, carrying its concealed markers
    public void CopyConcealed(string text) => Take(text, null, concealed: true);

    // the password manager clears its copy, leaving the clipboard empty
    public void AutoClear() => Take(null, null);

    // whether the clipboard carries the concealed markers, from a copy or Hydra's write
    public bool Concealed { get; private set; }

    // an image copy, with the text representation some apps put beside it
    public void CopyImage(byte[] png, string? alongside = null) => Take(alongside, png);

    public void Select(string text)
    {
        if (_os != ClipboardOs.Linux) throw new InvalidOperationException("only X11 has a selection to make");
        _x11Primary = text;
    }

    // onto a slave from the master, as InputRouter offers it with its ClipboardMaster and the slave's ClipboardSlave takes
    // it: the clipboard's hash with the selection beside it, and the whole clipboard only when the slave asks. A push
    // over the stream threshold streams, and midway runs between its Begin and its last chunk
    public void CarryTo(SimulatedClipboard slave, Action? midway = null)
    {
        if (_asMaster.Hash() is not { } hash || slave._asSlave.OnHash(RoundTrip(MessageKind.ClipboardHash, hash), "master") == PushNeed.None) return;
        if (Deliver(MessageKind.ClipboardPush, _asMaster.Push()!, slave._fromMaster, "master", midway) is { } push) slave._asSlave.OnPush(push, "master");
    }

    // back from this slave to the master: the master pulls, this answers, and the master takes the answer; an answer over
    // the stream threshold streams, with midway as CarryTo's. The answer this slave gave
    public ClipboardPullResponseMessage ReturnTo(SimulatedClipboard master, Action? midway = null)
    {
        // the visit began with the entry's read, which the pull goes against
        master._asMaster.Hash();
        var pull = RoundTrip(MessageKind.ClipboardPull, master._asMaster.Pull("slave"));
        var answer = _asSlave.OnPull(pull, "master");
        if (Deliver(MessageKind.ClipboardPullResponse, answer, master._fromSlave, "slave", midway) is { } delivered)
            master._asMaster.TakePullAnswer("slave", delivered);
        return answer;
    }

    private static T RoundTrip<T>(MessageKind kind, T message) => MessageSerializer.Decode(MessageSerializer.Encode(kind, message)).Deserialize<T>();

    // a message as ClipboardStreamSender sends it and the far side's receiver takes it: whole when small, else a Begin, its
    // chunks, and what they complete, if they do
    private T? Deliver<T>(MessageKind kind, T message, ClipboardStreamReceiver receiver, string from, Action? midway) where T : class, IClipboardPayload
    {
        var blob = MessageSerializer.Encode(kind, message);
        if (blob.Length <= ClipboardStreamSender.Threshold)
        {
            if (midway != null) throw new InvalidOperationException($"a {kind} of {blob.Length} bytes is not streamed, so has no midway");
            return MessageSerializer.Decode(blob).Deserialize<T>();
        }

        var contentHash = ClipboardUtils.ClipboardHash(ClipboardWire.From(message, Log, "stream"));
        var pullId = (message as ClipboardPullResponseMessage)?.PullId;
        var stream = StreamedClipboard.Split(Interlocked.Increment(ref _streamIds), blob, ClipboardStreamSender.ChunkBytes, kind, contentHash, pullId: pullId);
        receiver.OnBegin(from, RoundTrip(MessageKind.ClipboardStreamBegin, stream.Begin));
        DecodedMessage? completed = null;
        for (var i = 0; i < stream.Chunks.Length; i++)
        {
            if (i == stream.Chunks.Length - 1) midway?.Invoke();
            completed = receiver.OnChunk(from, RoundTrip(MessageKind.ClipboardStreamChunk, stream.Chunks[i]));
        }
        return completed?.Deserialize<T>();
    }

    private ulong? PendingFromMaster(string host) => _fromMaster.PendingFrom(host);

    public ClipboardRead ReadAll() => new(_owned ? null : _text, _owned ? null : ImagePng, null, null, Concealed);

    public bool CanUseEchoFallback() => _owned;

    // every copy and every one of Hydra's writes moves it, as the OS counter (X11: Hydra's owner-change counter) does
    public long? ChangeCount() => _version;

    public string? GetPrimaryText() => _os == ClipboardOs.Linux ? _x11Primary : _copied.Read();

    public bool SetPrimaryText(string text)
    {
        if (_os == ClipboardOs.Linux) _x11Primary = text;
        else _copied.Relay(text);
        return true;
    }

    // keeps only text and image, as Hydra's write leaves them here; the rest it takes as written, so echoes match
    public ClipboardSnapshot? SetClipboard(ClipboardSnapshot contents)
    {
        if (_os == ClipboardOs.Linux)
        {
            if (contents.PrimaryText != null) _x11Primary = contents.PrimaryText;
            WriteClipboard(contents);
            return contents;
        }
        return _copied.Write(contents.PrimaryText, leftAt =>
        {
            WriteClipboard(contents);
            leftAt(_version);
            return true;
        }) ? contents : null;
    }

    private void WriteClipboard(ClipboardSnapshot contents)
    {
        _text = contents.Text;
        ImagePng = contents.ImagePng;
        Concealed = contents.Concealed;
        ClipboardWrites++;
        _version++;
        _owned = true;
    }

    // a password manager's copy is no text copy, so never the primary
    private string? ReadTextCopy() => ImagePng != null || Concealed ? null : _text;

    private void Take(string? text, byte[]? png, bool concealed = false)
    {
        _text = text;
        ImagePng = png;
        Concealed = concealed;
        _version++;
        _owned = false;
    }
}

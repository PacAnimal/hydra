using Hydra.Platform;
using Hydra.Relay;
using Microsoft.Extensions.Logging.Abstractions;

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
    private long _version;
    private bool _owned;
    private string? _x11Primary;

    public SimulatedClipboard(ClipboardOs os)
    {
        _os = os;
        _copied = new CopiedPrimary(() => _version, ReadTextCopy, Log);
        _asSlave = new ClipboardSlave(this, Log);
        _asMaster = new ClipboardMaster(this, Log);
    }

    public string? Text { get; private set; }
    public byte[]? ImagePng { get; private set; }
    public string? Primary => GetPrimaryText();
    public int ClipboardWrites { get; private set; }

    public void Copy(string text) => Take(text, null);

    // an image copy, with the text representation some apps put beside it
    public void CopyImage(byte[] png, string? alongside = null) => Take(alongside, png);

    public void Select(string text)
    {
        if (_os != ClipboardOs.Linux) throw new InvalidOperationException("only X11 has a selection to make");
        _x11Primary = text;
    }

    // onto a slave from the master, as InputRouter offers it with its ClipboardMaster and the slave's ClipboardSlave takes
    // it: the clipboard's hash with the selection beside it, and the whole clipboard only when the slave asks
    public void CarryTo(SimulatedClipboard slave)
    {
        if (_asMaster.Hash() is not { } hash || slave._asSlave.OnHash(RoundTrip(MessageKind.ClipboardHash, hash), "master") == PushNeed.None) return;
        slave._asSlave.OnPush(RoundTrip(MessageKind.ClipboardPush, _asMaster.Push()!), "master");
    }

    // back from this slave to the master: the master pulls, this answers, and the master takes the answer
    public void ReturnTo(SimulatedClipboard master)
    {
        var pull = RoundTrip(MessageKind.ClipboardPull, master._asMaster.Pull("slave"));
        master._asMaster.OnPullAnswer("slave", RoundTrip(MessageKind.ClipboardPullResponse, _asSlave.OnPull(pull, "master")));
    }

    private static T RoundTrip<T>(MessageKind kind, T message) => MessageSerializer.Decode(MessageSerializer.Encode(kind, message)).Deserialize<T>();

    public string? GetText() => _owned ? null : Text;

    public byte[]? GetImagePng() => _owned ? null : ImagePng;

    public bool CanUseEchoFallback() => _os == ClipboardOs.Linux || _owned;

    public string? GetPrimaryText() => _os == ClipboardOs.Linux ? _x11Primary : _copied.Read();

    public bool SetPrimaryText(string text)
    {
        if (_os == ClipboardOs.Linux) _x11Primary = text;
        else _copied.Relay(text);
        return true;
    }

    public bool SetClipboard(ClipboardSnapshot contents)
    {
        if (_os == ClipboardOs.Linux)
        {
            if (contents.PrimaryText != null) _x11Primary = contents.PrimaryText;
            return WriteClipboard(contents);
        }
        return _copied.Write(contents.PrimaryText, leftAt =>
        {
            WriteClipboard(contents);
            leftAt(_version);
            return true;
        });
    }

    private bool WriteClipboard(ClipboardSnapshot contents)
    {
        Text = contents.Text;
        ImagePng = contents.ImagePng;
        ClipboardWrites++;
        _version++;
        _owned = true;
        return true;
    }

    private string? ReadTextCopy() => ImagePng != null ? null : Text;

    private void Take(string? text, byte[]? png)
    {
        Text = text;
        ImagePng = png;
        _version++;
        _owned = false;
    }
}

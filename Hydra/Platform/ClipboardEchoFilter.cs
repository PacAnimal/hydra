using System.IO.Hashing;

namespace Hydra.Platform;

// what reads back as Hydra's own write, while owned says the clipboard is still that write
internal sealed class ClipboardEchoFilter(Func<bool> owned)
{
    private string? _lastText;
    private ulong? _lastImageHash;
    private ulong? _lastHtmlHash;
    private ulong? _lastRtfHash;

    // what Hydra has just written, so reading it back is an echo; every format, since the write replaced them all
    public void Track(ClipboardSnapshot written)
    {
        _lastText = written.Text;
        _lastImageHash = Hash(written.ImagePng);
        _lastHtmlHash = Hash(written.Html);
        _lastRtfHash = Hash(written.Rtf);
    }
    public string? FilterText(string? text) => text != null && text == _lastText && owned() ? null : text;
    public byte[]? FilterImage(byte[]? png) => Echoes(_lastImageHash, Hash(png)) ? null : png;
    public string? FilterHtml(string? html) => Echoes(_lastHtmlHash, Hash(html)) ? null : html;
    public byte[]? FilterRtf(byte[]? rtf) => Echoes(_lastRtfHash, Hash(rtf)) ? null : rtf;

    private bool Echoes(ulong? written, ulong? read) => written != null && read == written && owned();
    private static ulong? Hash(byte[]? data) => data == null ? null : XxHash64.HashToUInt64(data);
    private static ulong? Hash(string? text) => text == null ? null : ClipboardUtils.StableHash(text);
}

using System.Text;
using Hydra.Platform;
using Microsoft.Extensions.Logging;

namespace Hydra.Relay;

// What a host compares a peer's clipboard against: its clipboard's hash, and the text and selection a selection-only
// answer resolves against. Never the clipboard itself, which may be a large image.
public sealed record ClipboardBaseline(ulong? Hash, string? Text, string? PrimaryText)
{
    public static ClipboardBaseline Of(ClipboardSnapshot s) => new(ClipboardUtils.ClipboardHash(s), s.Text, s.PrimaryText);

    // the selection alone, against a peer that offers no clipboard, so this host's need not be read
    public static ClipboardBaseline SelectionOf(IClipboardSync sync) => new(null, null, sync.GetPrimaryText());
}

// what a slave's hash check found it needs from the master: nothing, the selection alone, or the clipboard
public enum PushNeed { None, Selection, Clipboard }

// Clipboard snapshots on the wire, and the steps of the handshake that ClipboardSlave and ClipboardMaster take.
// A primary that repeats the text is the PrimaryIsText flag, so a copy's text crosses once. The selection travels beside
// the clipboard's hash: inline when small, else as its own hash, so a change to it alone never rewrites a clipboard, and
// one that fits inline never resends one.
public static class ClipboardWire
{
    private const int InlineSelectionBytes = 4096;

    public static ClipboardPushMessage ToPush(ClipboardSnapshot s) =>
        new(s.Text ?? "", WirePrimary(s), s.ImagePng, s.Html, s.Rtf, PrimaryIsText(s));

    private static ClipboardPullResponseMessage ToPullResponse(ClipboardSnapshot s, ClipboardPullMessage pull) =>
        new(pull.PullId, s.Text, WirePrimary(s), s.ImagePng, Html: s.Html, Rtf: s.Rtf, PrimaryIsText: PrimaryIsText(s));

    public static ClipboardHashMessage ToHash(ClipboardSnapshot s)
    {
        var hash = s.HasClipboard ? ClipboardUtils.ClipboardHash(s) : (ulong?)null;
        var primary = WirePrimary(s);
        return primary == null || Encoding.UTF8.GetByteCount(primary) <= InlineSelectionBytes
            ? new ClipboardHashMessage(hash, primary, PrimaryIsText(s))
            : new ClipboardHashMessage(hash, PrimaryHash: SelectionHash(primary));
    }

    public static ClipboardPullMessage ToPull(ClipboardBaseline against, int pullId) => new(pullId, against.Hash, SelectionHash(against.PrimaryText));

    // context names the message and its sender, for the logs
    public static ClipboardSnapshot From(IClipboardPayload m, ILogger log, string context) =>
        ValidateFields(m.Text, Primary(m.Text, m.PrimaryText, m.PrimaryIsText), m.ImagePng, m.Html, m.Rtf, log, context);

    // for the logs of both sides
    public static string Describe(IClipboardPayload m) =>
        $"{(m is ClipboardPullResponseMessage { Unchanged: true } ? "unchanged, " : "")}text={m.Text?.Length}, " +
        $"primary={(m.PrimaryIsText == true ? "the text" : m.PrimaryText?.Length)}, image={m.ImagePng?.Length}";

    // a master's hash against this host's clipboard: takes the selection beside a clipboard it already has, and says what
    // it still needs pushed
    internal static PushNeed NeedsPush(ClipboardHashMessage m, IClipboardSync sync, ClipboardBaseline local, ILogger log, string host)
    {
        if (m.Hash is { } hash && hash != local.Hash) return PushNeed.Clipboard;
        if (m.PrimaryHash is { } selectionHash) return selectionHash != SelectionHash(local.PrimaryText) ? PushNeed.Selection : PushNeed.None;
        TakeSelectionBeside(sync, m, local, log, $"selection from {host}");
        return PushNeed.None;
    }

    // the push that check asked for; with selectionFor, it was asked for the selection alone, and the clipboard, unless the
    // master's has changed since, is left as it is (a baseline without a clipboard hash takes the push whole). What it
    // applied, if anything
    internal static ClipboardSnapshot? ApplyPush(IClipboardSync sync, ClipboardPushMessage push, ClipboardBaseline? selectionFor, ILogger log, string host)
    {
        var context = $"push from {host}";
        var received = From(push, log, context);
        return selectionFor != null && ClipboardUtils.ClipboardHash(received) == selectionFor.Hash
            ? TakeSelectionBeside(sync, push, selectionFor, log, context)
            : ClipboardUtils.ApplyPreservingFiles(sync, received, log, context);
    }

    // a slave's answer to a pull: unchanged when its clipboard matches the master's, with its selection beside it only
    // when that differs, else the whole clipboard
    internal static ClipboardPullResponseMessage AnswerPull(ClipboardSnapshot local, ClipboardPullMessage pull)
    {
        if (pull.MasterHash != ClipboardUtils.ClipboardHash(local)) return ToPullResponse(local, pull);
        return SelectionHash(local.PrimaryText) == pull.MasterPrimaryHash
            ? new ClipboardPullResponseMessage(pull.PullId, null, Unchanged: true)
            : new ClipboardPullResponseMessage(pull.PullId, null, WirePrimary(local), Unchanged: true, PrimaryIsText: PrimaryIsText(local));
    }

    // the master's side of that answer, against what it pulled with: the selection alone beside an unchanged
    // clipboard, else the whole clipboard, which differs. What it applied, if anything
    internal static ClipboardSnapshot? TakePullAnswer(IClipboardSync sync, ClipboardPullResponseMessage answer, ClipboardBaseline pulledAgainst, ILogger log,
        string host)
    {
        var context = $"pull response from {host}";
        return answer.Unchanged == true
            ? TakeSelectionBeside(sync, answer, pulledAgainst, log, context)
            : ClipboardUtils.ApplyPreservingFiles(sync, From(answer, log, context), log, context);
    }

    // the selection beside a clipboard this host already has, set if it differs; a primary repeating the text is the
    // receiver's own text. What it applied, if anything
    private static ClipboardSnapshot? TakeSelectionBeside(IClipboardSync sync, IClipboardSelection m, ClipboardBaseline beside, ILogger log, string context)
    {
        var selection = Fit(Primary(beside.Text, m.PrimaryText, m.PrimaryIsText), "primary text", log, context);
        return selection != beside.PrimaryText && ClipboardSnapshot.SelectionOnly(selection) is { } alone
            ? ClipboardUtils.ApplyPreservingFiles(sync, alone, log, context)
            : null;
    }

    // null-out any field that individually exceeds the limit
    private static ClipboardSnapshot ValidateFields(string? text, string? primaryText, byte[]? image, string? html, byte[]? rtf, ILogger log, string context) =>
        new(Fit(text, "text", log, context), Fit(primaryText, "primary text", log, context), Fit(image, "image", log, context),
            Fit(html, "html", log, context), Fit(rtf, "rtf", log, context));

    // the text if it is within the limit, else null and a warning; empty is null
    private static string? Fit(string? text, string what, ILogger log, string context) =>
        string.IsNullOrEmpty(text) ? null : Fits(Encoding.UTF8.GetByteCount(text), what, log, context) ? text : null;

    private static byte[]? Fit(byte[]? data, string what, ILogger log, string context) =>
        data != null && Fits(data.Length, what, log, context) ? data : null;

    private static bool Fits(long bytes, string what, ILogger log, string context)
    {
        if (bytes <= ClipboardUtils.MaxClipboardBytes) return true;
        log.LogWarning("Clipboard {Context}: {What} exceeds {Max} bytes, dropping", context, what, ClipboardUtils.MaxClipboardBytes);
        return false;
    }

    private static ulong? SelectionHash(string? selection) => selection == null ? null : ClipboardUtils.StableHash(selection);
    private static string? WirePrimary(ClipboardSnapshot s) => s.SelectionIsText ? null : s.PrimaryText;
    private static bool? PrimaryIsText(ClipboardSnapshot s) => s.SelectionIsText ? true : null;
    private static string? Primary(string? text, string? primaryText, bool? primaryIsText) => primaryIsText == true ? text : primaryText;
}

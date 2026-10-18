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

    public static ClipboardPullResponseMessage ToPullResponse(ClipboardSnapshot s, ClipboardPullMessage? pull) =>
        new(s.Text, WirePrimary(s), s.ImagePng, Html: s.Html, Rtf: s.Rtf, PrimaryIsText: PrimaryIsText(s), PullId: pull?.PullId);

    public static ClipboardHashMessage ToHash(ClipboardSnapshot s)
    {
        var hash = s.HasClipboard ? ClipboardUtils.ClipboardHash(s) : (ulong?)null;
        var primary = WirePrimary(s);
        return primary == null || Encoding.UTF8.GetByteCount(primary) <= InlineSelectionBytes
            ? new ClipboardHashMessage(hash, primary, PrimaryIsText(s))
            : new ClipboardHashMessage(hash, PrimaryHash: SelectionHash(primary));
    }

    public static ClipboardPullMessage ToPull(ClipboardBaseline against, int pullId) => new(against.Hash, SelectionHash(against.PrimaryText), pullId);

    public static ClipboardSnapshot From(IClipboardPayload m, ILogger log, string context, string host) =>
        ClipboardUtils.ValidateFields(m.Text, Primary(m.Text, m.PrimaryText, m.PrimaryIsText), m.ImagePng, m.Html, m.Rtf, log, context, host);

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
        TakeSelectionBeside(sync, m.PrimaryText, m.PrimaryIsText, local, log, $"selection from {host}", host);
        return PushNeed.None;
    }

    // the push that check asked for; with selectionFor, it was asked for the selection alone, and the clipboard, unless the
    // master's has changed since, is left as it is (a baseline without a clipboard hash takes the push whole). What it
    // applied, if anything
    internal static ClipboardSnapshot? ApplyPush(IClipboardSync sync, ClipboardPushMessage push, ClipboardBaseline? selectionFor, ILogger log, string host)
    {
        var received = From(push, log, "push", host);
        var beside = selectionFor != null && ClipboardUtils.ClipboardHash(received) == selectionFor.Hash ? selectionFor : null;
        return Take(sync, push, received, beside, log, $"push from {host}", host);
    }

    // a slave's answer to a pull: unchanged when its clipboard matches the master's, with its selection beside it only
    // when that differs, else the whole clipboard
    internal static ClipboardPullResponseMessage AnswerPull(ClipboardSnapshot local, ClipboardPullMessage? pull)
    {
        if (pull?.MasterHash != ClipboardUtils.ClipboardHash(local)) return ToPullResponse(local, pull);
        return SelectionHash(local.PrimaryText) == pull.MasterPrimaryHash
            ? new ClipboardPullResponseMessage(null, Unchanged: true, PullId: pull.PullId)
            : new ClipboardPullResponseMessage(null, WirePrimary(local), Unchanged: true, PrimaryIsText: PrimaryIsText(local), PullId: pull.PullId);
    }

    // the master's side of that answer, against what it pulled with: the selection alone beside an unchanged
    // clipboard, else the whole clipboard, which differs. What it applied, if anything
    internal static ClipboardSnapshot? TakePullAnswer(IClipboardSync sync, ClipboardPullResponseMessage answer, ClipboardBaseline pulledAgainst, ILogger log,
        string host) =>
        Take(sync, answer, null, answer.Unchanged == true ? pulledAgainst : null, log, $"pull response from {host}", host);

    // a clipboard from a peer, whole unless it comes beside one this host already has. What it applied, if anything
    private static ClipboardSnapshot? Take(IClipboardSync sync, IClipboardPayload m, ClipboardSnapshot? whole, ClipboardBaseline? beside, ILogger log,
        string context, string host) =>
        beside == null
            ? ClipboardUtils.ApplyPreservingFiles(sync, whole ?? From(m, log, context, host), log, context)
            : TakeSelectionBeside(sync, m.PrimaryText, m.PrimaryIsText, beside, log, context, host);

    // the selection beside a clipboard this host already has, set if it differs; a primary repeating the text is the
    // receiver's own text. What it applied, if anything
    private static ClipboardSnapshot? TakeSelectionBeside(IClipboardSync sync, string? primaryText, bool? primaryIsText, ClipboardBaseline beside,
        ILogger log, string context, string host)
    {
        var selection = ClipboardUtils.ValidateFields(null, Primary(beside.Text, primaryText, primaryIsText), null, null, null, log, context, host)
            .PrimaryText;
        return selection != beside.PrimaryText && ClipboardSnapshot.SelectionOnly(selection) is { } alone
            ? ClipboardUtils.ApplyPreservingFiles(sync, alone, log, context)
            : null;
    }

    private static ulong? SelectionHash(string? selection) => selection == null ? null : ClipboardUtils.StableHash(selection);
    private static string? WirePrimary(ClipboardSnapshot s) => s.SelectionIsText ? null : s.PrimaryText;
    private static bool? PrimaryIsText(ClipboardSnapshot s) => s.SelectionIsText ? true : null;
    private static string? Primary(string? text, string? primaryText, bool? primaryIsText) => primaryIsText == true ? text : primaryText;
}

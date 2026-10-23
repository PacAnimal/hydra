using System.Text;
using Hydra.Platform;
using Hydra.Relay;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;
using Tests.Setup;

namespace Tests.Relay.Edge;

// whatever TrimToFit lets through must be receivable. TrimToFit counts UTF-8 (and raw image) bytes against
// MaxClipboardBytes, but the stream carries the message as JSON, where escaping and base64 make it larger; the receiver
// rejects a Begin over its own cap, so every clipboard at the cap, of any content, must encode within it
[TestFixture]
public class ClipboardEdgeEncodingTests
{
    private const string Host = "master-pc";
    private const string Context = "encoding";

    private static readonly int Cap = (int)ClipboardUtils.MaxClipboardBytes;

    [Test]
    public void CjkTextAtTheCapIsReceivable()
    {
        // U+4E2D, three bytes in UTF-8
        var text = Fill("\u4E2D", Cap);
        AssertReceivable(Trimmed(text, null, null), "CJK text");
    }

    [Test]
    public void EmojiTextAtTheCapIsReceivable()
    {
        // U+1F600, a surrogate pair, four bytes in UTF-8
        var text = Fill("\U0001F600", Cap);
        AssertReceivable(Trimmed(text, null, null), "emoji text");
    }

    [Test]
    public void HtmlOfMarkupCharactersAtTheCapIsReceivable()
    {
        var html = Fill("<>&\"'", Cap);
        AssertReceivable(Trimmed("x", null, html), "markup html");
    }

    // TrimToFit caps the fields together, so an image beside html at the cap shares it between them
    [Test]
    public void ImageBesideHtmlAtTheCapIsReceivable()
    {
        var image = new byte[Cap / 2];
        Random.Shared.NextBytes(image);
        var html = Fill("<>&\"'", Cap - image.Length - 1);
        AssertReceivable(Trimmed("x", image, html), "image beside markup html");
    }

    // TrimToFit itself lets no more than the cap through, whatever the fields
    [Test]
    public void ImageAndHtmlEachAtTheCapAreTrimmedToOne()
    {
        var image = new byte[Cap];
        var html = Fill("<", Cap);

        var trimmed = ClipboardUtils.TrimToFit("x", null, image, html, null, NullLogger.Instance, Context);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(trimmed.ImagePng, Is.Null);
            Assert.That(trimmed.Html, Is.Null);
            Assert.That(trimmed.Text, Is.EqualTo("x"));
        }
    }

    // the snapshot as TrimToFit returns it, which must have kept every field given
    private static ClipboardSnapshot Trimmed(string text, byte[]? image, string? html)
    {
        var trimmed = ClipboardUtils.TrimToFit(text, null, image, html, null, NullLogger.Instance, Context);
        using (Assert.EnterMultipleScope())
        {
            Assert.That(trimmed.Text, Is.SameAs(text), "TrimToFit dropped the text");
            Assert.That(trimmed.ImagePng, Is.SameAs(image), "TrimToFit dropped the image");
            Assert.That(trimmed.Html, Is.SameAs(html), "TrimToFit dropped the html");
        }
        return trimmed;
    }

    // streams the snapshot's push, as a sender would, into a receiver, which must hand back the whole message
    private static void AssertReceivable(ClipboardSnapshot snapshot, string what)
    {
        var push = ClipboardWire.ToPush(snapshot);
        var blob = MessageSerializer.Encode(MessageKind.ClipboardPush, push);
        var utf8 = Encoding.UTF8.GetByteCount(push.Text) + (push.Html is { } h ? Encoding.UTF8.GetByteCount(h) : 0) + (push.ImagePng?.Length ?? 0);
        TestContext.Out.WriteLine($"{what}: {utf8:N0} clipboard bytes encode to {blob.Length:N0} bytes ({(double)blob.Length / utf8:F2}x), " +
            $"receiver cap {2L * Cap:N0}");

        var failed = 0;
        using var receiver = new ClipboardStreamReceiver(new FakeClipboardSync(), (_, _) => BeginDecision.Cleared, _ => BeginDecision.Cleared,
            new FakeTimeProvider(), NullLogger.Instance);
        receiver.Failed += _ => failed++;
        var stream = StreamedClipboard.Split(1, blob, ClipboardStreamSender.ChunkBytes, MessageKind.ClipboardPush, ClipboardUtils.ClipboardHash(snapshot));

        receiver.OnBegin(Host, stream.Begin);
        DecodedMessage? completed = null;
        foreach (var chunk in stream.Chunks)
        {
            if (failed > 0) break;
            completed = receiver.OnChunk(Host, chunk);
        }

        Assert.That(failed, Is.Zero, $"the receiver rejected {what}: {blob.Length:N0} encoded bytes");
        var received = completed?.Deserialize<ClipboardPushMessage>();
        Assert.That(received, Is.Not.Null, $"the receiver never completed {what}");
        using (Assert.EnterMultipleScope())
        {
            Assert.That(received!.Text, Is.EqualTo(push.Text));
            Assert.That(received.Html, Is.EqualTo(push.Html));
            Assert.That(received.ImagePng, Is.EqualTo(push.ImagePng));
        }
    }

    // unit repeated to as many UTF-8 bytes as fit within bytes
    private static string Fill(string unit, int bytes)
    {
        var count = bytes / Encoding.UTF8.GetByteCount(unit);
        return new StringBuilder(unit.Length * count).Insert(0, unit, count).ToString();
    }
}

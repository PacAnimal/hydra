using Hydra.Platform;

namespace Tests.Setup;

public sealed class FakeClipboardSync : IClipboardSync
{
    public string? Text { get; private set; }
    public string? PrimaryText { get; private set; }
    public byte[]? ImagePng { get; private set; }
    public string? Html { get; private set; }
    public byte[]? Rtf { get; private set; }
    public int GetHtmlCallCount { get; private set; }
    public int GetRtfCallCount { get; private set; }
    public int GetTextCallCount { get; private set; }
    public int SetTextCallCount { get; private set; }
    public int GetPrimaryTextCallCount { get; private set; }
    public int SetPrimaryTextCallCount { get; private set; }
    public int GetImagePngCallCount { get; private set; }
    public int SetImagePngCallCount { get; private set; }
    public int SetClipboardCallCount { get; private set; }

    public string? GetText()
    {
        GetTextCallCount++;
        return Text;
    }

    public void SetText(string text)
    {
        SetTextCallCount++;
        Text = text;
    }

    public string? GetPrimaryText()
    {
        GetPrimaryTextCallCount++;
        return PrimaryText;
    }

    public void SetPrimaryText(string text)
    {
        SetPrimaryTextCallCount++;
        PrimaryText = text;
    }

    public byte[]? GetImagePng()
    {
        GetImagePngCallCount++;
        return ImagePng;
    }

    public void SetImagePng(byte[] pngData)
    {
        SetImagePngCallCount++;
        ImagePng = pngData;
    }

    public string? GetHtml()
    {
        GetHtmlCallCount++;
        return Html;
    }

    public byte[]? GetRtf()
    {
        GetRtfCallCount++;
        return Rtf;
    }

    public void SetClipboard(ClipboardSnapshot contents)
    {
        SetClipboardCallCount++;
        // mirror real implementations: replace all formats with the snapshot
        Text = contents.Text;
        PrimaryText = contents.PrimaryText;
        ImagePng = contents.ImagePng;
        Html = contents.Html;
        Rtf = contents.Rtf;
    }

    // helper for test setup (bypasses call counter)
    public void SetupImage(byte[]? png) => ImagePng = png;
    public void SetupHtml(string? html) => Html = html;
    public void SetupRtf(byte[]? rtf) => Rtf = rtf;
}

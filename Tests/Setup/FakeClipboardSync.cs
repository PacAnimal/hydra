using Hydra.Platform;

namespace Tests.Setup;

public sealed class FakeClipboardSync : IClipboardSync
{
    public string? Text { get; private set; }
    public string? PrimaryText { get; private set; }
    public byte[]? ImagePng { get; private set; }
    public string? Html { get; private set; }
    public byte[]? Rtf { get; private set; }
    public int GetTextCallCount { get; private set; }
    public int GetPrimaryTextCallCount { get; private set; }
    public int GetImagePngCallCount { get; private set; }
    public int SetClipboardCallCount { get; private set; }
    public bool HasFileClipboardValue { get; set; }
    public bool CanUseEchoFallbackValue { get; set; } = true;
    public bool WriteFailsValue { get; set; }
    public bool KeepsSelectionValue { get; set; } = true;

    public string? GetText()
    {
        GetTextCallCount++;
        return Text;
    }

    public string? GetPrimaryText()
    {
        GetPrimaryTextCallCount++;
        return PrimaryText;
    }

    public bool SetPrimaryText(string text)
    {
        if (!KeepsSelectionValue) return false;
        PrimaryText = text;
        return true;
    }

    public byte[]? GetImagePng()
    {
        GetImagePngCallCount++;
        return ImagePng;
    }

    public string? GetHtml() => Html;

    public byte[]? GetRtf() => Rtf;

    public bool HasFileClipboard() => HasFileClipboardValue;

    public bool CanUseEchoFallback() => CanUseEchoFallbackValue;

    public bool SetClipboard(ClipboardSnapshot contents)
    {
        SetClipboardCallCount++;
        // mirror real implementations: the selection beside it, and every format replaced
        if (contents.PrimaryText != null) SetPrimaryText(contents.PrimaryText);
        if (WriteFailsValue) return false;
        Text = contents.Text;
        ImagePng = contents.ImagePng;
        Html = contents.Html;
        Rtf = contents.Rtf;
        return true;
    }

    // helpers for test setup (bypass the call counters)
    public void SetupText(string text) => Text = text;
    public void SetupImage(byte[]? png) => ImagePng = png;
}

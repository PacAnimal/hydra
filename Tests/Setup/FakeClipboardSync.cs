using Hydra.Platform;

namespace Tests.Setup;

public sealed class FakeClipboardSync : IClipboardSync
{
    public string? Text { get; private set; }
    public string? PrimaryText { get; private set; }
    public byte[]? ImagePng { get; private set; }
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

    public void SetClipboard(string? text, string? primaryText, byte[]? imagePng)
    {
        SetClipboardCallCount++;
        if (text != null) Text = text;
        if (primaryText != null) PrimaryText = primaryText;
        if (imagePng != null) ImagePng = imagePng;
    }

    // helper for test setup (bypasses call counter)
    public void SetupImage(byte[]? png) => ImagePng = png;
}

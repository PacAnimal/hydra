using Hydra.Platform;

namespace Tests.Setup;

public sealed class FakeClipboardSync : IClipboardSync
{
    public string? Text { get; private set; }
    public string? PrimaryText { get; private set; }
    public int GetTextCallCount { get; private set; }
    public int SetTextCallCount { get; private set; }
    public int GetPrimaryTextCallCount { get; private set; }
    public int SetPrimaryTextCallCount { get; private set; }

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
}

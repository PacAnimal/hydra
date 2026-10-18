using Hydra.Platform;
using Microsoft.Extensions.Logging.Abstractions;
using Tests.Setup;

namespace Tests.Platform;

// The primary selection a platform without one keeps, against its real clipboard: a second instance of the platform's
// IClipboardSync stands in for another app, since to the first it is one.
public abstract class CopiedPrimaryContract
{
    protected static readonly byte[] Png = TestImages.Png;

    protected IClipboardSync Hydra { get; private set; } = null!;
    private IClipboardSync _otherApp = null!;

    protected abstract IClipboardSync Create();

    [SetUp]
    public void CreateBoth()
    {
        Hydra = Create();
        _otherApp = Create();
    }

    [TearDown]
    public void DisposeBoth()
    {
        (Hydra as IDisposable)?.Dispose();
        (_otherApp as IDisposable)?.Dispose();
    }

    // Hydra's write with a relayed selection beside it, then whatever the test has another app do
    protected void HydraWritesThen(Action otherApp)
    {
        Hydra.SetClipboard(new ClipboardSnapshot("written", "relayed selection", null));
        otherApp();
    }

    protected void OtherAppCopies(string? text, byte[]? png = null) => _otherApp.SetClipboard(new ClipboardSnapshot(text, null, png));

    [Test]
    public void OwnWrite_ReadsBackAsAnEcho()
    {
        HydraWritesThen(() => { });

        using (Assert.EnterMultipleScope())
        {
            Assert.That(Hydra.GetText(), Is.Null);
            Assert.That(Hydra.CanUseEchoFallback(), Is.True);
        }
    }

    [Test]
    public void SameTextCopiedAgain_IsNoEcho()
    {
        HydraWritesThen(() => OtherAppCopies("written"));

        using (Assert.EnterMultipleScope())
        {
            Assert.That(Hydra.GetText(), Is.EqualTo("written"));
            Assert.That(Hydra.CanUseEchoFallback(), Is.False);
        }
    }

    [Test]
    public void OwnWrite_LeavesTheRelayedSelection()
    {
        HydraWritesThen(() => _otherApp.GetText()); // another app pasting it is no copy

        Assert.That(Hydra.GetPrimaryText(), Is.EqualTo("relayed selection"));
    }

    [Test]
    public void TextCopyAfterwards_IsThePrimary()
    {
        HydraWritesThen(() => OtherAppCopies("copied"));

        Assert.That(Hydra.GetPrimaryText(), Is.EqualTo("copied"));
    }

    [Test]
    public void SameTextCopiedAgain_IsThePrimary()
    {
        HydraWritesThen(() => OtherAppCopies("written"));

        Assert.That(Hydra.GetPrimaryText(), Is.EqualTo("written"));
    }

    [Test]
    public void ImageCopyAfterwards_LeavesTheRelayedSelection()
    {
        HydraWritesThen(() => OtherAppCopies("text beside the image", Png));

        Assert.That(Hydra.GetPrimaryText(), Is.EqualTo("relayed selection"));
    }

    [Test]
    public void SelectionOnly_LeavesTheClipboardAndOutranksAnEarlierCopy()
    {
        OtherAppCopies("copied first");
        ClipboardUtils.ApplyPreservingFiles(Hydra, new ClipboardSnapshot(null, "selected later", null), NullLogger.Instance, "test");

        using (Assert.EnterMultipleScope())
        {
            Assert.That(Hydra.GetText(), Is.EqualTo("copied first"));
            Assert.That(Hydra.GetPrimaryText(), Is.EqualTo("selected later"));
        }
    }
}

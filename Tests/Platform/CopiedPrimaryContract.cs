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
    public void ChangeCount_MovesOnAWriteAndOnAnotherAppsCopy_NotOnAPaste()
    {
        var start = Hydra.ChangeCount();
        Hydra.SetClipboard(new ClipboardSnapshot("written", null, null));
        var afterWrite = Hydra.ChangeCount();
        _otherApp.ReadAll();
        var afterPaste = Hydra.ChangeCount();
        OtherAppCopies("written");
        var afterCopy = Hydra.ChangeCount();

        using (Assert.EnterMultipleScope())
        {
            Assert.That(start, Is.Not.Null);
            Assert.That(afterWrite, Is.Not.EqualTo(start));
            Assert.That(afterPaste, Is.EqualTo(afterWrite));
            Assert.That(afterCopy, Is.Not.EqualTo(afterWrite), "an identical copy moves it too");
        }
    }

    [Test]
    public void WroteAt_IsTheCountRightAfterTheWrite_AndAForeignCopyMovesPastIt()
    {
        Hydra.SetClipboard(new ClipboardSnapshot("written", null, null));
        var afterWrite = Hydra.ChangeCount();
        var wroteAt = Hydra.WroteAt();
        OtherAppCopies("written");

        using (Assert.EnterMultipleScope())
        {
            Assert.That(wroteAt, Is.Not.Null.And.EqualTo(afterWrite));
            Assert.That(Hydra.WroteAt(), Is.EqualTo(wroteAt), "another app's copy is no write of Hydra's");
            Assert.That(Hydra.ChangeCount(), Is.GreaterThan(wroteAt ?? long.MaxValue));
        }
    }

    [Test]
    public void WroteAt_FollowsAClear()
    {
        Hydra.SetClipboard(new ClipboardSnapshot("written", null, null));
        Hydra.ClearClipboard();

        Assert.That(Hydra.WroteAt(), Is.EqualTo(Hydra.ChangeCount()));
    }

    [Test]
    public void OwnWrite_ReadsBackAsAnEcho()
    {
        HydraWritesThen(() => { });

        using (Assert.EnterMultipleScope())
        {
            Assert.That(Hydra.ReadAll().Text, Is.Null);
            Assert.That(Hydra.CanUseEchoFallback(), Is.True);
        }
    }

    [Test]
    public void SameTextCopiedAgain_IsNoEcho()
    {
        HydraWritesThen(() => OtherAppCopies("written"));

        using (Assert.EnterMultipleScope())
        {
            Assert.That(Hydra.ReadAll().Text, Is.EqualTo("written"));
            Assert.That(Hydra.CanUseEchoFallback(), Is.False);
        }
    }

    [Test]
    public void OwnWrite_LeavesTheRelayedSelection()
    {
        HydraWritesThen(() => _otherApp.ReadAll()); // another app pasting it is no copy

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
    public void Clear_LeavesThePlaceholderForOtherAppsAndNothingForHydra()
    {
        Hydra.SetClipboard(new ClipboardSnapshot("written", "relayed selection", Png));

        Hydra.ClearClipboard();

        using (Assert.EnterMultipleScope())
        {
            Assert.That(_otherApp.ReadAll().Text, Is.EqualTo(ClipboardSnapshot.PlaceholderText));
            Assert.That(_otherApp.ReadAll().ImagePng, Is.Null);
            Assert.That(Hydra.CanUseEchoFallback(), Is.True);
            Assert.That(ClipboardUtils.ReadWithFallback(Hydra, null, NullLogger.Instance, "test")!.HasClipboard, Is.False);
            Assert.That(_otherApp.GetPrimaryText(), Is.Not.EqualTo(ClipboardSnapshot.PlaceholderText), "the placeholder is never a text copy");
            Assert.That(Hydra.GetPrimaryText(), Is.EqualTo("relayed selection"));
        }
    }

    // its no-history markers read like a password manager's, which it is not: Hydra's read finds no copy there
    [Test]
    public void Clear_IsNeverConcealed()
    {
        Hydra.SetClipboard(new ClipboardSnapshot("hunter2", null, null, Concealed: true));

        Hydra.ClearClipboard();

        using (Assert.EnterMultipleScope())
        {
            Assert.That(ClipboardUtils.ReadWithFallback(_otherApp, null, NullLogger.Instance, "test"), Is.EqualTo(ClipboardSnapshot.Empty));
            Assert.That(ClipboardUtils.ReadWithFallback(Hydra, null, NullLogger.Instance, "test")!.Concealed, Is.False);
        }
    }

    [Test]
    public void Write_ReportsEveryFormatItTook()
    {
        var contents = new ClipboardSnapshot("written", "relayed selection", null, Html: "<b>written</b>");

        Assert.That(Hydra.SetClipboard(contents), Is.EqualTo(contents));
    }

    [Test]
    public void SelectionOnly_LeavesTheClipboardAndOutranksAnEarlierCopy()
    {
        OtherAppCopies("copied first");
        ClipboardUtils.ApplyPreservingFiles(Hydra, new ClipboardSnapshot(null, "selected later", null), NullLogger.Instance, "test");

        using (Assert.EnterMultipleScope())
        {
            Assert.That(Hydra.ReadAll().Text, Is.EqualTo("copied first"));
            Assert.That(Hydra.GetPrimaryText(), Is.EqualTo("selected later"));
        }
    }

    [Test]
    public void ConcealedWrite_ReadsAsConcealedInAnotherApp()
    {
        Hydra.SetClipboard(new ClipboardSnapshot("hunter2", null, null, Concealed: true));
        var read = ClipboardUtils.ReadWithFallback(_otherApp, null, NullLogger.Instance, "test");

        using (Assert.EnterMultipleScope())
        {
            Assert.That(_otherApp.ReadAll().Concealed, Is.True);
            Assert.That(read?.Text, Is.EqualTo("hunter2"));
            Assert.That(read?.Concealed, Is.True);
        }
    }

    [Test]
    public void PlainWrite_ReadsAsNotConcealedInAnotherApp()
    {
        Hydra.SetClipboard(new ClipboardSnapshot("plain", null, null));

        using (Assert.EnterMultipleScope())
        {
            Assert.That(_otherApp.ReadAll().Concealed, Is.False);
            Assert.That(ClipboardUtils.ReadWithFallback(_otherApp, null, NullLogger.Instance, "test")!.Concealed, Is.False);
        }
    }
}

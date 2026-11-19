using Hydra.Platform.Linux.Wayland;

namespace Tests.Platform.Wayland;

// whether a session restored from its token without the clipboard is started again without the token. pure, so these
// run everywhere.
[TestFixture]
public class ClipboardRestoreRetryTests
{
    [Test]
    public void RestoredWithoutClipboard_Retries() =>
        Assert.That(ClipboardRestoreRetry.ShouldRetry(usedToken: true, askedClipboard: true, clipboardSupported: true, clipboardEnabled: false, alreadyRetried: false), Is.True);

    [Test]
    public void RestoredWithClipboard_DoesNotRetry() =>
        Assert.That(ClipboardRestoreRetry.ShouldRetry(usedToken: true, askedClipboard: true, clipboardSupported: true, clipboardEnabled: true, alreadyRetried: false), Is.False);

    [Test]
    public void FreshSessionWithoutClipboard_DoesNotRetry() =>
        Assert.That(ClipboardRestoreRetry.ShouldRetry(usedToken: false, askedClipboard: true, clipboardSupported: true, clipboardEnabled: false, alreadyRetried: false), Is.False);

    [Test]
    public void ClipboardNotAskedFor_DoesNotRetry() =>
        Assert.That(ClipboardRestoreRetry.ShouldRetry(usedToken: true, askedClipboard: false, clipboardSupported: true, clipboardEnabled: false, alreadyRetried: false), Is.False);

    [Test]
    public void ClipboardUnsupported_AsOnInputCaptureVersion1_DoesNotRetry() =>
        Assert.That(ClipboardRestoreRetry.ShouldRetry(usedToken: true, askedClipboard: true, clipboardSupported: false, clipboardEnabled: false, alreadyRetried: false), Is.False);

    [Test]
    public void AlreadyRetriedThisRun_DoesNotRetryAgain() =>
        Assert.That(ClipboardRestoreRetry.ShouldRetry(usedToken: true, askedClipboard: true, clipboardSupported: true, clipboardEnabled: false, alreadyRetried: true), Is.False);
}

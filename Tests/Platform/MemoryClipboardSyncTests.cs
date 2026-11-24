using Hydra.Platform;
using Microsoft.Extensions.Logging.Abstractions;

namespace Tests.Platform;

[TestFixture]
public class MemoryClipboardSyncTests
{
    [Test]
    public void Clear_ReadsAsEmpty_KeepsTheSelection_AndMovesTheCounter()
    {
        IClipboardSync clipboard = new MemoryClipboardSync();
        var copy = new ClipboardSnapshot("copied", "selected", null);
        clipboard.SetClipboard(copy);
        var afterCopy = clipboard.ChangeCount();
        var cleared = clipboard.ClearClipboard();

        using (Assert.EnterMultipleScope())
        {
            Assert.That(cleared, Is.True);
            Assert.That(ClipboardUtils.ReadWithFallback(clipboard, null, NullLogger.Instance, "test")?.HasClipboard, Is.False);
            Assert.That(clipboard.GetPrimaryText(), Is.EqualTo("selected"));
            Assert.That(clipboard.ChangeCount(), Is.EqualTo(afterCopy + 1));
        }
    }
}

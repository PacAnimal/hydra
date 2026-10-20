using Hydra.Platform;
using Hydra.Relay;
using Microsoft.Extensions.Logging.Abstractions;
using Tests.Setup;

namespace Tests.Clipboard;

// EchoFallback and ClipboardSlave on their own, against a fake clipboard
[TestFixture]
public class ClipboardHandshakeTests
{
    private static readonly NullLogger Log = NullLogger.Instance;

    [Test]
    public void EchoFallback_SelectionOnly_KeepsTheClipboardFallback()
    {
        // the fake reads nothing back, as a host does its own write
        var echo = new EchoFallback(new FakeClipboardSync(), Log);
        echo.Remember(new ClipboardSnapshot("written", null, null, Html: "<b>written</b>"));

        echo.Remember(new ClipboardSnapshot(null, "new selection", null));

        Assert.That(echo.Read("test").Html, Is.EqualTo("<b>written</b>"));
    }

    [Test]
    public void EchoFallback_Clipboard_ReplacesTheFallback()
    {
        var echo = new EchoFallback(new FakeClipboardSync(), Log);
        echo.Remember(new ClipboardSnapshot("old", "selection", null));

        echo.Remember(new ClipboardSnapshot("new", null, null));

        Assert.That(echo.Read("test").Text, Is.EqualTo("new"));
    }

    [Test]
    public void SlaveHashCheck_MasterWithNoClipboard_ReadsOnlyTheSelection()
    {
        var sync = new FakeClipboardSync();
        sync.SetupImage([1, 2, 3]);
        var slave = new ClipboardSlave(sync, Log);

        var need = slave.OnHash(new ClipboardHashMessage(null, "selected on the master"), "master");

        using (Assert.EnterMultipleScope())
        {
            Assert.That(need, Is.EqualTo(PushNeed.None));
            Assert.That(sync.PrimaryText, Is.EqualTo("selected on the master"));
            Assert.That(sync.GetImagePngCallCount, Is.Zero);
            Assert.That(sync.GetTextCallCount, Is.Zero);
        }
    }
}

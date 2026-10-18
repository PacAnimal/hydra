using Hydra.Platform;
using Hydra.Relay;
using Microsoft.Extensions.Logging.Abstractions;

namespace Tests.Clipboard;

// how a snapshot is put on the wire and read back
[TestFixture]
public class ClipboardWireTests
{
    private static readonly NullLogger Log = NullLogger.Instance;

    [Test]
    public void PrimaryRepeatingTheText_IsSentOnce()
    {
        var push = ClipboardWire.ToPush(new ClipboardSnapshot("copied", "copied", null));
        var response = ClipboardWire.AnswerPull(new ClipboardSnapshot("copied", "copied", null), new ClipboardPullMessage(1));

        using (Assert.EnterMultipleScope())
        {
            Assert.That(push.PrimaryText, Is.Null);
            Assert.That(push.PrimaryIsText, Is.True);
            Assert.That(ClipboardWire.From(push, Log, "push from peer").PrimaryText, Is.EqualTo("copied"));
            Assert.That(response.PrimaryText, Is.Null);
            Assert.That(ClipboardWire.From(response, Log, "pull response from peer").PrimaryText, Is.EqualTo("copied"));
        }
    }

    [Test]
    public void PrimaryOfItsOwn_IsSentAsIs()
    {
        var push = ClipboardWire.ToPush(new ClipboardSnapshot("copied", "selected", null));

        using (Assert.EnterMultipleScope())
        {
            Assert.That(push.PrimaryText, Is.EqualTo("selected"));
            Assert.That(push.PrimaryIsText, Is.Null);
        }
    }
}

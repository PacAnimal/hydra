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

    [Test]
    public void ConcealedCopy_CrossesConcealed_BothWays()
    {
        var concealed = new ClipboardSnapshot("hunter2", null, null, Concealed: true);
        var push = RoundTrip(MessageKind.ClipboardPush, ClipboardWire.ToPush(concealed));
        var answer = RoundTrip(MessageKind.ClipboardPullResponse, ClipboardWire.AnswerPull(concealed, new ClipboardPullMessage(1)));

        using (Assert.EnterMultipleScope())
        {
            Assert.That(push.Concealed, Is.True);
            Assert.That(ClipboardWire.From(push, Log, "push from peer").Concealed, Is.True);
            Assert.That(answer.Concealed, Is.True);
            Assert.That(ClipboardWire.From(answer, Log, "pull response from peer").Concealed, Is.True);
        }
    }

    [Test]
    public void PlainCopy_CarriesNoConcealedFlag()
    {
        var push = ClipboardWire.ToPush(new ClipboardSnapshot("plain", null, null));

        using (Assert.EnterMultipleScope())
        {
            Assert.That(push.Concealed, Is.Null);
            Assert.That(ClipboardWire.From(push, Log, "push from peer").Concealed, Is.False);
        }
    }

    [Test]
    public void ConcealedCopy_IsDescribedWithoutItsText()
    {
        var described = ClipboardWire.Describe(ClipboardWire.ToPush(new ClipboardSnapshot("hunter2", null, null, Concealed: true)));

        using (Assert.EnterMultipleScope())
        {
            Assert.That(described, Does.Contain("concealed"));
            Assert.That(described, Does.Not.Contain("hunter2"));
        }
    }

    [Test]
    public void ClearedFlags_SurviveTheWire()
    {
        var answer = RoundTrip(MessageKind.ClipboardPullResponse, new ClipboardPullResponseMessage(1, null, Cleared: true));
        var hash = RoundTrip(MessageKind.ClipboardHash, new ClipboardHashMessage(null, Cleared: true));

        using (Assert.EnterMultipleScope())
        {
            Assert.That(answer.Cleared, Is.True);
            Assert.That(hash.Cleared, Is.True);
        }
    }

    private static T RoundTrip<T>(MessageKind kind, T message) => MessageSerializer.Decode(MessageSerializer.Encode(kind, message)).Deserialize<T>();
}

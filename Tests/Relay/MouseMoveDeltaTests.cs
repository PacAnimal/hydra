using Cathedral.Extensions;
using Hydra.Relay;

namespace Tests.Relay;

[TestFixture]
public class MouseMoveDeltaTests
{
    [Test]
    public void MouseMoveDeltaMessage_RoundTrip()
    {
        var original = new MouseMoveDeltaMessage(42, -17);
        var payload = MessageSerializer.Encode(MessageKind.MouseMoveDelta, original);
        var msg = MessageSerializer.Decode(payload);
        var json = msg.Json;

        Assert.That(msg.Kind, Is.EqualTo(MessageKind.MouseMoveDelta));
        var decoded = json.FromSaneJson<MouseMoveDeltaMessage>();
        Assert.That(decoded, Is.Not.Null);
        using (Assert.EnterMultipleScope())
        {
            Assert.That(decoded!.Dx, Is.EqualTo(42));
            Assert.That(decoded.Dy, Is.EqualTo(-17));
        }
    }

    [TestCase(0, 0)]
    [TestCase(int.MaxValue, int.MinValue)]
    [TestCase(-1, 1)]
    public void MouseMoveDeltaMessage_ExtremeValues_RoundTrip(int dx, int dy)
    {
        var original = new MouseMoveDeltaMessage(dx, dy);
        var payload = MessageSerializer.Encode(MessageKind.MouseMoveDelta, original);
        var json = MessageSerializer.Decode(payload).Json;
        var decoded = json.FromSaneJson<MouseMoveDeltaMessage>();

        Assert.That(decoded, Is.Not.Null);
        using (Assert.EnterMultipleScope())
        {
            Assert.That(decoded!.Dx, Is.EqualTo(dx));
            Assert.That(decoded.Dy, Is.EqualTo(dy));
        }
    }

    [Test]
    public void MessageKind_MouseMoveDelta_Is10()
    {
        Assert.That((byte)MessageKind.MouseMoveDelta, Is.EqualTo(10));
    }
}

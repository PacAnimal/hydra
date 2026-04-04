using Cathedral.Extensions;
using Hydra.Keyboard;
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
        var (kind, json) = MessageSerializer.Decode(payload);

        Assert.That(kind, Is.EqualTo(MessageKind.MouseMoveDelta));
        var decoded = json.FromSaneJson<MouseMoveDeltaMessage>();
        Assert.That(decoded, Is.Not.Null);
        using (Assert.EnterMultipleScope())
        {
            Assert.That(decoded!.DX, Is.EqualTo(42));
            Assert.That(decoded.DY, Is.EqualTo(-17));
        }
    }

    [TestCase(0, 0)]
    [TestCase(int.MaxValue, int.MinValue)]
    [TestCase(-1, 1)]
    public void MouseMoveDeltaMessage_ExtremeValues_RoundTrip(int dx, int dy)
    {
        var original = new MouseMoveDeltaMessage(dx, dy);
        var payload = MessageSerializer.Encode(MessageKind.MouseMoveDelta, original);
        var (_, json) = MessageSerializer.Decode(payload);
        var decoded = json.FromSaneJson<MouseMoveDeltaMessage>();

        Assert.That(decoded, Is.Not.Null);
        using (Assert.EnterMultipleScope())
        {
            Assert.That(decoded!.DX, Is.EqualTo(dx));
            Assert.That(decoded.DY, Is.EqualTo(dy));
        }
    }

    [Test]
    public void KeyEventMessage_WithRepeat_RoundTrip()
    {
        var original = new KeyEventMessage(KeyEventType.KeyDown, KeyModifiers.None, 'w', null, 500, 33);
        var payload = MessageSerializer.Encode(MessageKind.KeyEvent, original);
        var (kind, json) = MessageSerializer.Decode(payload);

        Assert.That(kind, Is.EqualTo(MessageKind.KeyEvent));
        var decoded = json.FromSaneJson<KeyEventMessage>();
        Assert.That(decoded, Is.Not.Null);
        using (Assert.EnterMultipleScope())
        {
            Assert.That(decoded!.Type, Is.EqualTo(KeyEventType.KeyDown));
            Assert.That(decoded.Character, Is.EqualTo('w'));
            Assert.That(decoded.RepeatDelayMs, Is.EqualTo(500));
            Assert.That(decoded.RepeatRateMs, Is.EqualTo(33));
        }
    }

    [Test]
    public void KeyEventMessage_WithoutRepeat_RoundTrip()
    {
        var original = new KeyEventMessage(KeyEventType.KeyUp, KeyModifiers.None, 'w', null);
        var payload = MessageSerializer.Encode(MessageKind.KeyEvent, original);
        var (_, json) = MessageSerializer.Decode(payload);
        var decoded = json.FromSaneJson<KeyEventMessage>();

        Assert.That(decoded, Is.Not.Null);
        using (Assert.EnterMultipleScope())
        {
            Assert.That(decoded!.RepeatDelayMs, Is.Null, "KeyUp should not carry repeat settings");
            Assert.That(decoded.RepeatRateMs, Is.Null);
        }
    }

    [Test]
    public void MessageKind_MouseMoveDelta_Is10()
    {
        Assert.That((byte)MessageKind.MouseMoveDelta, Is.EqualTo(10));
    }
}

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

    [Test]
    public void RelayCoalescing_AccumulatesRelativeDeltas()
    {
        var first = MessageSerializer.Encode(MessageKind.MouseMoveDelta, new MouseMoveDeltaMessage(12, -4));
        var second = MessageSerializer.Encode(MessageKind.MouseMoveDelta, new MouseMoveDeltaMessage(-3, 9));

        var coalesced = RelayConnection.TryCoalesceMovement(first, second, out var payload);
        var decoded = MessageSerializer.Decode(payload).Deserialize<MouseMoveDeltaMessage>();

        using (Assert.EnterMultipleScope())
        {
            Assert.That(coalesced, Is.True);
            Assert.That(decoded, Is.EqualTo(new MouseMoveDeltaMessage(9, 5)));
        }
    }

    [Test]
    public void RelayCoalescing_SaturatesExtremeRelativeDeltas()
    {
        var first = MessageSerializer.Encode(MessageKind.MouseMoveDelta,
            new MouseMoveDeltaMessage(int.MaxValue, int.MinValue));
        var second = MessageSerializer.Encode(MessageKind.MouseMoveDelta, new MouseMoveDeltaMessage(1, -1));

        Assert.That(RelayConnection.TryCoalesceMovement(first, second, out var payload), Is.True);
        Assert.That(MessageSerializer.Decode(payload).Deserialize<MouseMoveDeltaMessage>(),
            Is.EqualTo(new MouseMoveDeltaMessage(int.MaxValue, int.MinValue)));
    }

    [Test]
    public void RelayCoalescing_DoesNotCrossMovementKindsOrControlMessages()
    {
        var relative = MessageSerializer.Encode(MessageKind.MouseMoveDelta, new MouseMoveDeltaMessage(1, 2));
        var absolute = MessageSerializer.Encode(MessageKind.MouseMove, new MouseMoveMessage("screen", 3, 4));
        var key = MessageSerializer.Encode(MessageKind.KeyEvent,
            new KeyEventMessage(KeyEventType.KeyDown, KeyModifiers.None, 'a', null));

        using (Assert.EnterMultipleScope())
        {
            Assert.That(RelayConnection.TryCoalesceMovement(relative, absolute, out _), Is.False);
            Assert.That(RelayConnection.TryCoalesceMovement(relative, key, out _), Is.False);
        }
    }
}

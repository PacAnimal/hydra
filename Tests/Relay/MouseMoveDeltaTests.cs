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

    [Test]
    public void RelayCoalescing_AccumulatesRelativeDeltas()
    {
        var batch = RelayConnection.MovementBatch.CreateDelta(["slave"], 12, -4);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(batch.TryAppendDelta(["slave"], -3, 9), Is.True);
            Assert.That(MessageSerializer.Decode(batch.Snapshot()).Deserialize<MouseMoveDeltaMessage>(), Is.EqualTo(new MouseMoveDeltaMessage(9, 5)));
        }
    }

    [Test]
    public void RelayCoalescing_SaturatesExtremeRelativeDeltas()
    {
        var batch = RelayConnection.MovementBatch.CreateDelta(["slave"], int.MaxValue, int.MinValue);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(batch.TryAppendDelta(["slave"], 1, -1), Is.True);
            Assert.That(MessageSerializer.Decode(batch.Snapshot()).Deserialize<MouseMoveDeltaMessage>(),
                Is.EqualTo(new MouseMoveDeltaMessage(int.MaxValue, int.MinValue)));
        }
    }

    [Test]
    public void RelayCoalescing_DoesNotCrossMovementKindsOrTargets()
    {
        var absolute = MessageSerializer.Encode(MessageKind.MouseMove, new MouseMoveMessage("screen", 3, 4));
        var relative = RelayConnection.MovementBatch.CreateDelta(["slave"], 1, 2);
        var positioned = RelayConnection.MovementBatch.CreateAbsolute(["slave"], absolute);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(relative.TryAppendAbsolute(["slave"], absolute), Is.False);
            Assert.That(positioned.TryAppendDelta(["slave"], 1, 2), Is.False);
            Assert.That(relative.TryAppendDelta(["other"], 1, 2), Is.False);
        }
    }
}

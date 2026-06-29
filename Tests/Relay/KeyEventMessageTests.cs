using Cathedral.Extensions;
using Hydra.Keyboard;
using Hydra.Relay;

namespace Tests.Relay;

[TestFixture]
public class KeyEventMessageTests
{
    [Test]
    public void KeyEventMessage_WithRepeat_RoundTrip()
    {
        var original = new KeyEventMessage(KeyEventType.KeyDown, KeyModifiers.None, 'w', null, 500, 33);
        var payload = MessageSerializer.Encode(MessageKind.KeyEvent, original);
        var msg = MessageSerializer.Decode(payload);
        var json = msg.Json;

        Assert.That(msg.Kind, Is.EqualTo(MessageKind.KeyEvent));
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
        var json = MessageSerializer.Decode(payload).Json;
        var decoded = json.FromSaneJson<KeyEventMessage>();

        Assert.That(decoded, Is.Not.Null);
        using (Assert.EnterMultipleScope())
        {
            Assert.That(decoded!.RepeatDelayMs, Is.Null, "KeyUp should not carry repeat settings");
            Assert.That(decoded.RepeatRateMs, Is.Null);
        }
    }

    [Test]
    public void KeyEventMessage_IsRepeat_True_RoundTrip()
    {
        var original = new KeyEventMessage(KeyEventType.KeyDown, KeyModifiers.None, 'w', null, IsRepeat: true);
        var decoded = MessageSerializer.Decode(MessageSerializer.Encode(MessageKind.KeyEvent, original)).Deserialize<KeyEventMessage>();
        Assert.That(decoded.IsRepeat, Is.True);
    }

    [Test]
    public void KeyEventMessage_IsRepeat_AbsentInJson_DefaultsFalse()
    {
        // older wire format without IsRepeat field should deserialize cleanly to false
        const string json = """{"Type":0,"Modifiers":0,"Character":"w","Key":null}""";
        var decoded = json.FromSaneJson<KeyEventMessage>();
        Assert.That(decoded?.IsRepeat, Is.False);
    }
}

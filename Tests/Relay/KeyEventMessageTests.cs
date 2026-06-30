using Cathedral.Extensions;
using Hydra.Keyboard;
using Hydra.Relay;

namespace Tests.Relay;

[TestFixture]
public class KeyEventMessageTests
{
    [Test]
    public void KeyEventMessage_RoundTrip()
    {
        var original = new KeyEventMessage(KeyEventType.KeyDown, KeyModifiers.None, 'w', null);
        var payload = MessageSerializer.Encode(MessageKind.KeyEvent, original);
        var msg = MessageSerializer.Decode(payload);

        Assert.That(msg.Kind, Is.EqualTo(MessageKind.KeyEvent));
        var decoded = msg.Json.FromSaneJson<KeyEventMessage>();
        Assert.That(decoded, Is.Not.Null);
        using (Assert.EnterMultipleScope())
        {
            Assert.That(decoded!.Type, Is.EqualTo(KeyEventType.KeyDown));
            Assert.That(decoded.Character, Is.EqualTo('w'));
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

    [Test]
    public void KeyEventMessage_UnicodeKeyRepeat_False_RoundTrip()
    {
        var original = new KeyEventMessage(KeyEventType.KeyDown, KeyModifiers.None, 'w', null, UnicodeKeyRepeat: false);
        var decoded = MessageSerializer.Decode(MessageSerializer.Encode(MessageKind.KeyEvent, original)).Deserialize<KeyEventMessage>();
        Assert.That(decoded.UnicodeKeyRepeat, Is.False);
    }

    [Test]
    public void KeyEventMessage_UnicodeKeyRepeat_AbsentInJson_DefaultsTrue()
    {
        // older wire format without UnicodeKeyRepeat field should default to the new behaviour
        const string json = """{"Type":0,"Modifiers":0,"Character":"w","Key":null}""";
        var decoded = json.FromSaneJson<KeyEventMessage>();
        Assert.That(decoded?.UnicodeKeyRepeat, Is.True);
    }
}

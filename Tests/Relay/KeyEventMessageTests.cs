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

    [TestCase('\u0439')] // й — Cyrillic, absent from every Latin layout
    [TestCase('€')]
    [TestCase('§')]
    public void KeyEventMessage_NonLatinCharacter_SurvivesTheWire(char ch)
    {
        // a slave with no matching layout injects these as unicode, so the char itself has to arrive
        // intact. resolving it to anything layout-specific on the master is what breaks that.
        var original = new KeyEventMessage(KeyEventType.KeyDown, KeyModifiers.None, ch, null);
        var decoded = MessageSerializer.Decode(MessageSerializer.Encode(MessageKind.KeyEvent, original)).Deserialize<KeyEventMessage>();
        using (Assert.EnterMultipleScope())
        {
            Assert.That(decoded.Character, Is.EqualTo(ch));
            Assert.That(decoded.Key, Is.Null);
        }
    }

    private static readonly string[] KeycodeTerms = ["vk", "virtualkey", "keycode", "scancode", "scan"];

    [Test]
    public void KeyEventMessage_CarriesNoOsKeycode()
    {
        // the protocol is deliberately char/SpecialKey only: a VK, scancode or keycode on the wire ties
        // injection to the master's layout, and the slave can no longer choose unicode injection for a
        // char its own layout lacks. see the keyboard design notes in CLAUDE.md.
        // asserted on the type's shape, not on serialized output — an all-null keycode field is omitted
        // from the json and would let the regression through.
        var offenders = typeof(KeyEventMessage).GetProperties()
            .Where(prop => KeycodeTerms.Any(banned => prop.Name.Replace("_", "").Contains(banned, StringComparison.OrdinalIgnoreCase)))
            .Select(prop => prop.Name)
            .ToArray();

        Assert.That(offenders, Is.Empty,
            $"KeyEventMessage must not carry OS keycodes: {string.Join(", ", offenders)}");
    }
}

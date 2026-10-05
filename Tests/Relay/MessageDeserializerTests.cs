using System.Text;
using System.Text.Json;
using Hydra.Relay;
using Microsoft.Extensions.Logging;

namespace Tests.Relay;

[TestFixture]
public class MessageDeserializerTests
{
    private static ReadOnlyMemory<byte> Body(string json) => Encoding.UTF8.GetBytes(json);

    [Test]
    public void DecodeBody_ReadsTheMessage() =>
        Assert.That(Body("""{"dx":3,"dy":-4}""").DecodeBody<MouseMoveDeltaMessage>(), Is.EqualTo(new MouseMoveDeltaMessage(3, -4)));

    [Test]
    public void DecodeBody_Throws_OnAMalformedBody() =>
        Assert.Throws<JsonException>(() => Body("{").DecodeBody<MouseMoveDeltaMessage>());

    [Test]
    public void TryDecodeBody_IsNull_OnAMalformedBody() =>
        Assert.That(Body("{").TryDecodeBody<MouseMoveDeltaMessage>(), Is.Null);

    [Test]
    public void TryDecodeBody_ReadsTheMessage() =>
        Assert.That(Body("""{"dx":3,"dy":-4}""").TryDecodeBody<MouseMoveDeltaMessage>(), Is.EqualTo(new MouseMoveDeltaMessage(3, -4)));

    // enums cross the relay by name, and a peer on an older build still spells this one "MacOS"
    [Test]
    public void DecodeBody_ReadsAPeerPlatform_InAnyCasing() =>
        Assert.That(Body("""{"screens":[],"platform":"MacOS"}""").DecodeBody<ScreenInfoMessage>()!.Platform, Is.EqualTo(PeerPlatform.MacOs));

    [Test]
    public void ParseMessage_IsNullAndWarns_OnAMalformedBody()
    {
        var log = new WarningCount();

        var parsed = Body("{").ParseMessage<MouseMoveDeltaMessage>(log, "test");

        using (Assert.EnterMultipleScope())
        {
            Assert.That(parsed, Is.Null);
            Assert.That(log.Warnings, Is.EqualTo(1));
        }
    }

    [Test]
    public void ParseMessage_ReadsTheMessage_WithoutWarning()
    {
        var log = new WarningCount();

        var parsed = Body("""{"dx":3,"dy":-4}""").ParseMessage<MouseMoveDeltaMessage>(log, "test");

        using (Assert.EnterMultipleScope())
        {
            Assert.That(parsed, Is.EqualTo(new MouseMoveDeltaMessage(3, -4)));
            Assert.That(log.Warnings, Is.Zero);
        }
    }

    private sealed class WarningCount : ILogger
    {
        public int Warnings { get; private set; }
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => true;
        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            if (logLevel == LogLevel.Warning) Warnings++;
        }
    }
}

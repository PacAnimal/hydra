using System.Text;
using System.Text.Json;
using Common;
using Hydra.Relay;
using Microsoft.Extensions.Logging.Abstractions;

namespace Tests;

[TestFixture]
public class NetworkConfigTests
{
    // ReSharper disable once StaticMemberInGenericType
    private static readonly JsonSerializerOptions PascalCaseOptions = new() { PropertyNamingPolicy = null };

    [Test]
    public void Parse_RoundTrip()
    {
        var original = new NetworkConfig("https://styx.example.com", "abc123", "encblobhere");
        var json = JsonSerializer.Serialize(original, PascalCaseOptions);
        var base64 = Convert.ToBase64String(Encoding.UTF8.GetBytes(json));

        var parsed = NetworkConfig.Parse(base64);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(parsed.StyxServer, Is.EqualTo(original.StyxServer));
            Assert.That(parsed.EncryptionKey, Is.EqualTo(original.EncryptionKey));
            Assert.That(parsed.Authorization, Is.EqualTo(original.Authorization));
        }
    }

    [Test]
    public void ServerUrl_ReturnsStyxServer()
    {
        var config = new NetworkConfig("https://relay.example.com", "key", "auth");
        Assert.That(config.ServerUrl, Is.EqualTo("https://relay.example.com"));
    }

    [Test]
    public void Parse_InvalidBase64_Throws()
    {
        Assert.Throws<FormatException>(() => NetworkConfig.Parse("!!!not-base64!!!"));
    }
}

[TestFixture]
public class RelayEncryptionTests
{
    [Test]
    public async Task Encrypt_Decrypt_RoundTrip()
    {
        var enc = new RelayEncryption("testkey");
        var dec = new RelayEncryption("testkey");
        var log = NullLogger.Instance;

        var payload = "Hello, World!"u8.ToArray();
        var encrypted = await enc.Encrypt(payload);
        var decrypted = await dec.Decrypt("peer", encrypted, log);

        Assert.That(decrypted, Is.EqualTo(payload));
    }

    [Test]
    public async Task Decrypt_AfterPeerReconnect_RecoversWithNewKey()
    {
        const string key = "mypassword";
        var sender1 = new RelayEncryption(key);
        var sender2 = new RelayEncryption(key); // simulates peer reconnecting (new GenerateKey salt)
        var receiver = new RelayEncryption(key);
        var log = NullLogger.Instance;

        // first message from sender1 — receiver caches its remote key
        var msg1 = await sender1.Encrypt("first"u8.ToArray());
        var dec1 = await receiver.Decrypt("peer", msg1, log);
        Assert.That(dec1, Is.EqualTo("first"u8.ToArray()));

        // peer reconnects — sender2 has a different salt
        var msg2 = await sender2.Encrypt("second"u8.ToArray());
        var dec2 = await receiver.Decrypt("peer", msg2, log);
        Assert.That(dec2, Is.EqualTo("second"u8.ToArray()));
    }

    [Test]
    public async Task MultiplePeers_EachDecryptedCorrectly()
    {
        const string key = "sharedkey";
        var peerA = new RelayEncryption(key);
        var peerB = new RelayEncryption(key);
        var receiver = new RelayEncryption(key);
        var log = NullLogger.Instance;

        var msgA = await peerA.Encrypt("from-a"u8.ToArray());
        var msgB = await peerB.Encrypt("from-b"u8.ToArray());

        // interleaved messages from two peers
        var decA1 = await receiver.Decrypt("peer-a", msgA, log);
        var decB1 = await receiver.Decrypt("peer-b", msgB, log);
        var decA2 = await receiver.Decrypt("peer-a", await peerA.Encrypt("from-a-again"u8.ToArray()), log);
        var decB2 = await receiver.Decrypt("peer-b", await peerB.Encrypt("from-b-again"u8.ToArray()), log);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(decA1, Is.EqualTo("from-a"u8.ToArray()));
            Assert.That(decB1, Is.EqualTo("from-b"u8.ToArray()));
            Assert.That(decA2, Is.EqualTo("from-a-again"u8.ToArray()));
            Assert.That(decB2, Is.EqualTo("from-b-again"u8.ToArray()));
        }
    }

    [Test]
    public async Task MultiplePeers_OnePeerReconnects_OtherKeyUnaffected()
    {
        const string key = "sharedkey";
        var peerA = new RelayEncryption(key);
        var peerB = new RelayEncryption(key);
        var peerBReconnected = new RelayEncryption(key); // new instance = new salt
        var receiver = new RelayEncryption(key);
        var log = NullLogger.Instance;

        // establish both peers
        var decA1 = await receiver.Decrypt("peer-a", await peerA.Encrypt("a-hello"u8.ToArray()), log);
        var decB1 = await receiver.Decrypt("peer-b", await peerB.Encrypt("b-hello"u8.ToArray()), log);

        // peer-b reconnects with a new key; peer-a keeps sending normally
        var decA2 = await receiver.Decrypt("peer-a", await peerA.Encrypt("a-after-b-reconnect"u8.ToArray()), log);
        var decB2 = await receiver.Decrypt("peer-b", await peerBReconnected.Encrypt("b-reconnected"u8.ToArray()), log);

        // both peers continue working correctly after the reconnect
        var decA3 = await receiver.Decrypt("peer-a", await peerA.Encrypt("a-final"u8.ToArray()), log);
        var decB3 = await receiver.Decrypt("peer-b", await peerBReconnected.Encrypt("b-final"u8.ToArray()), log);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(decA1, Is.EqualTo("a-hello"u8.ToArray()));
            Assert.That(decB1, Is.EqualTo("b-hello"u8.ToArray()));
            Assert.That(decA2, Is.EqualTo("a-after-b-reconnect"u8.ToArray()));
            Assert.That(decB2, Is.EqualTo("b-reconnected"u8.ToArray()));
            Assert.That(decA3, Is.EqualTo("a-final"u8.ToArray()));
            Assert.That(decB3, Is.EqualTo("b-final"u8.ToArray()));
        }
    }

    [Test]
    public async Task MessageSerializer_Encode_Decode_RoundTrip()
    {
        var move = new MouseMoveMessage(100, 200);
        var bytes = MessageSerializer.Encode(MessageKind.MouseMove, move);
        var (kind, json) = MessageSerializer.Decode(bytes);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(kind, Is.EqualTo(MessageKind.MouseMove));
            Assert.That(json, Does.Contain("100"));
        }
        Assert.That(json, Does.Contain("200"));
    }
}

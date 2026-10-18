using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Cathedral.Extensions;
using Common;
using Hydra.Relay;

namespace Tests.Relay;

[TestFixture]
public class NetworkConfigTests
{
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

    // the flag is written only when set, so a blob for any other relay is spelled as it always was
    [Test]
    public async Task OnlyAnEmbeddedBlobCarriesTheEmbeddedFlag()
    {
        var embedded = NetworkConfig.Parse(await NetworkConfig.ComputeEmbeddedBlob("https://relay:5000", "pw"));
        var externalJson = Encoding.UTF8.GetString(Convert.FromBase64String((embedded with { Embedded = false }).ToBlob()));

        using (Assert.EnterMultipleScope())
        {
            Assert.That(embedded.Embedded, Is.True);
            Assert.That(externalJson, Does.Not.Contain("embedded"));
        }
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

        var payload = "Hello, World!"u8.ToArray();
        var encrypted = await enc.Encrypt(payload);
        var decrypted = await dec.Decrypt("peer", encrypted);

        Assert.That(decrypted, Is.EqualTo(payload));
    }

    [Test]
    public async Task Decrypt_AfterPeerReconnect_RecoversWithNewKey()
    {
        const string key = "mypassword";
        var sender1 = new RelayEncryption(key);
        var sender2 = new RelayEncryption(key); // simulates peer reconnecting (new GenerateKey salt)
        var receiver = new RelayEncryption(key);

        // first message from sender1 — receiver caches its remote key
        var msg1 = await sender1.Encrypt("first"u8.ToArray());
        var dec1 = await receiver.Decrypt("peer", msg1);
        Assert.That(dec1, Is.EqualTo("first"u8.ToArray()));

        // peer reconnects — sender2 has a different salt
        var msg2 = await sender2.Encrypt("second"u8.ToArray());
        var dec2 = await receiver.Decrypt("peer", msg2);
        Assert.That(dec2, Is.EqualTo("second"u8.ToArray()));
    }

    [Test]
    public async Task Decrypt_JunkFrame_KeepsCachedKey()
    {
        const string key = "mypassword";
        var state = new WorldState();
        var sender = new RelayEncryption(key);
        var receiver = new RelayEncryption(key, state);

        await receiver.Decrypt("peer", await sender.Encrypt("first"u8.ToArray()));
        var good = await state.GetRemoteKey("peer");

        Assert.That(async () => await receiver.Decrypt("peer", RandomNumberGenerator.GetBytes(200)), Throws.Exception);

        var after = await state.GetRemoteKey("peer");
        using (Assert.EnterMultipleScope())
        {
            Assert.That(after?.AesGcmKey, Is.SameAs(good?.AesGcmKey));
            Assert.That(await receiver.Decrypt("peer", await sender.Encrypt("second"u8.ToArray())), Is.EqualTo("second"u8.ToArray()));
        }
    }

    [Test]
    public async Task Decrypt_JunkFirstFrame_CachesNothing()
    {
        var state = new WorldState();
        var receiver = new RelayEncryption("mypassword", state);

        Assert.That(async () => await receiver.Decrypt("peer", RandomNumberGenerator.GetBytes(200)), Throws.Exception);

        Assert.That(await state.GetRemoteKey("peer"), Is.Null);
    }

    [Test]
    public async Task Decrypt_TamperedFrameWithCachedSalt_KeepsCachedKey()
    {
        const string key = "mypassword";
        var state = new WorldState();
        var sender = new RelayEncryption(key);
        var receiver = new RelayEncryption(key, state);

        await receiver.Decrypt("peer", await sender.Encrypt("first"u8.ToArray()));
        var good = await state.GetRemoteKey("peer");

        var tampered = await sender.Encrypt("second"u8.ToArray());
        tampered[^1] ^= 0xFF;
        Assert.That(async () => await receiver.Decrypt("peer", tampered), Throws.Exception);

        var after = await state.GetRemoteKey("peer");
        Assert.That(after?.AesGcmKey, Is.SameAs(good?.AesGcmKey));
    }

    [Test]
    public async Task Decrypt_SameSalt_DerivesOnce()
    {
        const string key = "mypassword";
        var state = new WorldState();
        var sender = new RelayEncryption(key);
        var receiver = new RelayEncryption(key, state);

        await receiver.Decrypt("peer", await sender.Encrypt("first"u8.ToArray()));
        var first = await state.GetRemoteKey("peer");
        await receiver.Decrypt("peer", await sender.Encrypt("second"u8.ToArray()));

        var second = await state.GetRemoteKey("peer");
        Assert.That(second?.AesGcmKey, Is.SameAs(first?.AesGcmKey));
    }

    [Test]
    public async Task MultiplePeers_EachDecryptedCorrectly()
    {
        const string key = "sharedkey";
        var peerA = new RelayEncryption(key);
        var peerB = new RelayEncryption(key);
        var receiver = new RelayEncryption(key);

        var msgA = await peerA.Encrypt("from-a"u8.ToArray());
        var msgB = await peerB.Encrypt("from-b"u8.ToArray());

        // interleaved messages from two peers
        var decA1 = await receiver.Decrypt("peer-a", msgA);
        var decB1 = await receiver.Decrypt("peer-b", msgB);
        var decA2 = await receiver.Decrypt("peer-a", await peerA.Encrypt("from-a-again"u8.ToArray()));
        var decB2 = await receiver.Decrypt("peer-b", await peerB.Encrypt("from-b-again"u8.ToArray()));

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

        // establish both peers
        var decA1 = await receiver.Decrypt("peer-a", await peerA.Encrypt("a-hello"u8.ToArray()));
        var decB1 = await receiver.Decrypt("peer-b", await peerB.Encrypt("b-hello"u8.ToArray()));

        // peer-b reconnects with a new key; peer-a keeps sending normally
        var decA2 = await receiver.Decrypt("peer-a", await peerA.Encrypt("a-after-b-reconnect"u8.ToArray()));
        var decB2 = await receiver.Decrypt("peer-b", await peerBReconnected.Encrypt("b-reconnected"u8.ToArray()));

        // both peers continue working correctly after the reconnect
        var decA3 = await receiver.Decrypt("peer-a", await peerA.Encrypt("a-final"u8.ToArray()));
        var decB3 = await receiver.Decrypt("peer-b", await peerBReconnected.Encrypt("b-final"u8.ToArray()));

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
        var move = new MouseMoveMessage("", 100, 200);
        var bytes = MessageSerializer.Encode(MessageKind.MouseMove, move);
        var msg = MessageSerializer.Decode(bytes);
        var json = msg.Json;

        using (Assert.EnterMultipleScope())
        {
            Assert.That(msg.Kind, Is.EqualTo(MessageKind.MouseMove));
            Assert.That(json, Does.Contain("100"));
        }
        Assert.That(json, Does.Contain("200"));
    }

    [Test]
    public void MessageSerializer_MouseMove_Screen_SurvivesRoundTrip()
    {
        var original = new MouseMoveMessage("desktop:1", 500, 300);
        var bytes = MessageSerializer.Encode(MessageKind.MouseMove, original);
        var json = MessageSerializer.Decode(bytes).Json;
        var decoded = json.FromSaneJson<MouseMoveMessage>();

        Assert.That(decoded, Is.Not.Null);
        using (Assert.EnterMultipleScope())
        {
            Assert.That(decoded!.Screen, Is.EqualTo("desktop:1"));
            Assert.That(decoded.X, Is.EqualTo(500));
            Assert.That(decoded.Y, Is.EqualTo(300));
        }
    }
}

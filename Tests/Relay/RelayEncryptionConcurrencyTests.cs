using System.Collections.Concurrent;
using Hydra.Relay;
using Tests.Setup;

namespace Tests.Relay;

/// <summary>
/// Two lanes encrypt on one <see cref="RelayEncryption"/> at the same time, and no two messages may ever
/// share a nonce.
///
/// <para><b>This is the safety argument for the whole two-lane design, and until now it existed only as a
/// comment.</b> <c>RelayEncryption</c> draws ONE salt per connection and reuses it, so the AES-GCM key is
/// fixed for that connection's lifetime — which makes nonce uniqueness the only thing standing between us
/// and disaster. Reusing a nonce under one GCM key leaks the XOR of the two plaintexts and lets an attacker
/// forge messages; it is not a degraded mode, it is a broken one.</para>
///
/// <para><b>The guarantee comes from a package we do not control.</b> It is <c>Iv96.Next</c>'s
/// process-wide <c>Interlocked</c> counter, in Cathedral, and nothing else here would notice it changing.
/// What the assertions below actually catch is stated where each one sits — be precise about that, because
/// "this test covers the nonce" is the kind of belief that outlives the test's real reach.</para>
/// </summary>
[TestFixture]
public class RelayEncryptionConcurrencyTests
{
    /// <summary>The wire layout `SimpleAes` writes: [64 salt][12 nonce][16 tag][ciphertext].</summary>
    private const int SaltLength = 64;
    private const int NonceLength = 12;

    [Test]
    public async Task ConcurrentEncryptionNeverRepeatsANonce()
    {
        // TWO instances, as two connections on one host are. A single one could not tell a process-wide
        // counter from a per-instance one, which is one of the ways the guarantee could quietly go.
        var connections = new[] { new RelayEncryption("a key for the concurrency test"), new RelayEncryption("a second connection") };

        const int lanes = 8;
        const int perLane = 500;
        var total = lanes * perLane;

        var nonces = new ConcurrentBag<string>();
        var counters = new ConcurrentBag<long>();
        var salts = new ConcurrentBag<string>();

        await Task.WhenAll(Enumerable.Range(0, lanes).Select(lane => Task.Run(async () =>
        {
            for (var i = 0; i < perLane; i++)
            {
                var message = await connections[lane % connections.Length].Encrypt([(byte)lane, (byte)i]);
                var nonce = message.AsSpan(SaltLength, NonceLength);
                salts.Add(Convert.ToHexString(message.AsSpan(0, SaltLength)));
                nonces.Add(Convert.ToHexString(nonce));

                // The low 48 bits are the counter; the high 48 are a timestamp shared by anything in the
                // same millisecond, so the counter is the entire separation.
                long counter = 0;
                foreach (var b in nonce[6..]) counter = (counter << 8) | b;
                counters.Add(counter);
            }
        })));

        using (Assert.EnterMultipleScope())
        {
            Assert.That(nonces, Has.Count.EqualTo(total), "the premise — every message was counted");

            // WHAT THIS CATCHES, precisely: a counter that is not atomic, racing under eight concurrent
            // lanes. That is the risk two drain loops introduce, so it is the one worth a test.
            Assert.That(nonces.Distinct().Count(), Is.EqualTo(total),
                "two messages shared a nonce under one connection's fixed key — that leaks plaintext and allows forgery");

            // And what THIS catches, which distinctness alone cannot: a counter drawn at random (4 000
            // samples from 2^48 almost never collide, so uniqueness would pass), one that stalled, and one
            // that became per-instance — all three break a contiguous run across two connections.
            Assert.That(counters.Max() - counters.Min(), Is.EqualTo(total - 1),
                "the nonce counter is not one unbroken process-wide sequence any more — uniqueness may now be luck rather than design");

            // The premise for all of it: the salt is per CONNECTION, so the key is fixed and the nonce is
            // the only thing separating two messages. Were it per-message this would matter far less.
            Assert.That(salts.Distinct().Count(), Is.EqualTo(connections.Length),
                "the salt is per-connection by design; if this ever becomes per-message, revisit why nonce uniqueness is load-bearing");
        }
    }

    /// <summary>
    /// And what one instance encrypts concurrently, a peer can still read back. Uniqueness is worthless if
    /// the concurrency corrupted the output.
    /// </summary>
    [Test]
    public async Task ConcurrentlyEncryptedMessagesAllDecrypt()
    {
        const string key = "a key both ends share";
        var sender = new RelayEncryption(key);
        var receiver = new RelayEncryption(key);

        var payloads = Enumerable.Range(0, 200).Select(i => new byte[] { (byte)i, (byte)(i >> 8), 0xAB }).ToArray();

        var encrypted = await Task.WhenAll(payloads.Select(p => Task.Run(async () => await sender.Encrypt(p))));

        for (var i = 0; i < payloads.Length; i++)
        {
            var round = await receiver.Decrypt("sender", encrypted[i], TestLog.CreateLogger<RelayEncryptionConcurrencyTests>());
            Assert.That(round, Is.EqualTo(payloads[i]), $"message {i} did not survive concurrent encryption");
        }
    }
}

using Cathedral.Utils;

namespace Hydra.Relay;

public class RelayEncryption(string key, IWorldState? peerState = null)
{
    private readonly SimpleAes _aes = new(key);
    private readonly SimpleAesKey _localKey = SimpleAes.GenerateKey(key);
    private readonly IWorldState _peerState = peerState ?? new WorldState();

    /// <summary>
    /// Safe to call concurrently. One salt per connection keeps the AES-GCM key constant, so nonce uniqueness is
    /// the whole defence (GCM reuse leaks the XOR of two plaintexts and hands out forgeries), and it holds:
    /// <c>Iv96.Next</c> draws from a process-wide <c>Interlocked</c> counter, across lanes and instances alike.
    /// </summary>
    public async ValueTask<byte[]> Encrypt(byte[] payload, CancellationToken cancel = default) =>
        await _aes.Encrypt(payload, _localKey, cancel);

    // a peer's key is derived once per salt it sends, and cached only once a frame proves it
    public async ValueTask<byte[]> Decrypt(string sourceHost, byte[] payload, CancellationToken cancel = default)
    {
        var cached = await _peerState.GetRemoteKey(sourceHost);
        if (cached is { } known && payload.AsSpan().StartsWith(known.Salt))
            return await _aes.Decrypt(payload, known, false, cancel);

        var derived = SimpleAes.ExtractKey(key, payload);
        var decrypted = await _aes.Decrypt(payload, derived, false, cancel);
        await _peerState.SetRemoteKey(sourceHost, derived);
        return decrypted;
    }
}

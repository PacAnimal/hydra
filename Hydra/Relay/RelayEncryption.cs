using Cathedral.Utils;
using Microsoft.Extensions.Logging;

namespace Hydra.Relay;

public class RelayEncryption(string key, IWorldState? peerState = null)
{
    private readonly SimpleAes _aes = new(key);
    private readonly SimpleAesKey _localKey = SimpleAes.GenerateKey(key);
    private readonly IWorldState _peerState = peerState ?? new WorldState();

    public async Task<byte[]> Encrypt(byte[] payload, CancellationToken cancel = default) =>
        await _aes.Encrypt(payload, _localKey, cancel);

    public async Task<byte[]> Decrypt(string sourceHost, byte[] payload, ILogger log, CancellationToken cancel = default)
    {
        var cached = await _peerState.GetRemoteKey(sourceHost);
        var remoteKey = cached ?? SimpleAes.ExtractKey(key, payload);
        if (cached is null)
            await _peerState.SetRemoteKey(sourceHost, remoteKey);

        try
        {
            return await _aes.Decrypt(payload, remoteKey, false, cancel);
        }
        catch (Exception ex)
        {
            // salt mismatch or auth failure — remote peer may have reconnected with a new key
            log.LogDebug("Decrypt failed with cached remote key for {SourceHost} — re-deriving from message salt", sourceHost);
            try
            {
                remoteKey = SimpleAes.ExtractKey(key, payload);
                await _peerState.SetRemoteKey(sourceHost, remoteKey);
                return await _aes.Decrypt(payload, remoteKey, false, cancel);
            }
            catch (Exception retryEx)
            {
                log.LogDebug(retryEx, "Decrypt failed after key re-derivation for {SourceHost}", sourceHost);
                throw;
            }
        }
    }
}

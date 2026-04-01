using Cathedral.Utils;
using Microsoft.Extensions.Logging;

namespace Hydra.Relay;

public class RelayEncryption(string key)
{
    private readonly string _key = key;
    private readonly SimpleAes _aes = new(key);
    private readonly SimpleAesKey _localKey = SimpleAes.GenerateKey(key);
    private readonly Dictionary<string, SimpleAesKey> _remoteKeys = [];

    public async Task<byte[]> Encrypt(byte[] payload, CancellationToken cancel = default) =>
        await _aes.Encrypt(payload, _localKey, cancel);

    public async Task<byte[]> Decrypt(string sourceHost, byte[] payload, ILogger log, CancellationToken cancel = default)
    {
        if (!_remoteKeys.TryGetValue(sourceHost, out var remoteKey))
        {
            // first message from this host — derive key from the salt embedded in the payload
            remoteKey = SimpleAes.ExtractKey(_key, payload);
            _remoteKeys[sourceHost] = remoteKey;
        }

        try
        {
            return await _aes.Decrypt(payload, remoteKey, false, cancel);
        }
        catch (Exception ex)
        {
            // salt mismatch or auth failure — remote peer may have reconnected with a new key
            log.LogDebug(ex, "Decrypt failed with cached remote key for {SourceHost} — re-deriving from message salt", sourceHost);
            try
            {
                remoteKey = SimpleAes.ExtractKey(_key, payload);
                _remoteKeys[sourceHost] = remoteKey;
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

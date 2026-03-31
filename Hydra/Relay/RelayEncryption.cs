using Cathedral.Utils;
using Microsoft.Extensions.Logging;

namespace Hydra.Relay;

public class RelayEncryption(string key)
{
    private readonly string _key = key;
    private readonly SimpleAes _aes = new(key);
    private readonly SimpleAesKey _localKey = SimpleAes.GenerateKey(key);
    private SimpleAesKey? _remoteKey;

    public async Task<byte[]> Encrypt(byte[] payload, CancellationToken cancel = default) =>
        await _aes.Encrypt(payload, _localKey, cancel);

    public async Task<byte[]> Decrypt(byte[] payload, ILogger log, CancellationToken cancel = default)
    {
        if (_remoteKey == null)
        {
            // first message — derive remote key from the salt embedded in the payload
            _remoteKey = SimpleAes.ExtractKey(_key, payload);
        }

        try
        {
            return await _aes.Decrypt(payload, _remoteKey, false, cancel);
        }
        catch (Exception ex)
        {
            // salt mismatch or auth failure — remote peer may have reconnected with a new key
            log.LogDebug(ex, "Decrypt failed with cached remote key — re-deriving from message salt");
            try
            {
                _remoteKey = SimpleAes.ExtractKey(_key, payload);
                return await _aes.Decrypt(payload, _remoteKey, false, cancel);
            }
            catch (Exception retryEx)
            {
                log.LogWarning(retryEx, "Decrypt failed after key re-derivation — dropping message");
                throw;
            }
        }
    }
}

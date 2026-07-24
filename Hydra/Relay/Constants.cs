namespace Hydra.Relay;

internal static class Constants
{
    public const int ReconnectDelaySeconds = 15;
    public const int AuthTimeoutSeconds = 10; // cap the Authenticate round-trip so a stalled handshake retries
}

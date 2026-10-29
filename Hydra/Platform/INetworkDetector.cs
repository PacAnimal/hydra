namespace Hydra.Platform;

public interface INetworkDetector
{
    // connected SSIDs; empty = detection succeeded but no wifi; null = detection unavailable/failed
    // (unknown). Callers must NOT treat unknown as "no wifi" and drive a config restart off it.
    Task<List<string>?> GetActiveSsids(CancellationToken cancel = default);
    Task<bool?> GetIsPluggedIn(CancellationToken cancel = default);

    // throws when the platform could not be asked, which is unknown, never "no network"; gatewayMacs false leaves the
    // neighbour table unread and sends nothing
    Task<NetworkLinks> GetNetworkLinks(bool gatewayMacs, CancellationToken cancel = default);
}

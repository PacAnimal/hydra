namespace Hydra.Platform;

public interface INetworkDetector
{
    Task<List<string>> GetActiveSsids(CancellationToken cancel = default);
    Task<bool?> GetIsPluggedIn(CancellationToken cancel = default);
}

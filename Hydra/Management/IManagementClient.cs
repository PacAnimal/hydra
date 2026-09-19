namespace Hydra.Management;

// Shared by ManagementClient (talks to the real local daemon over the versioned framed-JSON
// protocol) and MockManagementClient (returns canned data for `hydra tui --demo`), so the TUI
// itself never knows or cares which one it's talking to.
internal interface IManagementClient
{
    Task<ServerHello> HelloAsync(CancellationToken cancel = default);
    Task<HydraStatusSnapshot> GetStatusAsync(CancellationToken cancel = default);
    Task<ManagementLogPage> GetLogsAsync(long after, CancellationToken cancel = default);
    Task<ConfigDocument> GetConfigAsync(CancellationToken cancel = default);
    Task<ConfigValidation> ValidateConfigAsync(string json, CancellationToken cancel = default);
    Task<ConfigDocument> SaveConfigAsync(SaveConfigRequest request, CancellationToken cancel = default);
    Task<CommandResult> ReconnectRelayAsync(CancellationToken cancel = default);
    Task<CommandResult> RestartHydraAsync(CancellationToken cancel = default);
    Task<CommandResult> ShutdownHydraAsync(CancellationToken cancel = default);
    Task<RemotePairResult> PairRemoteAsync(RemotePairRequest request, CancellationToken cancel = default);
    Task<RemoteConfigDocument> GetRemoteConfigAsync(string host, CancellationToken cancel = default);
    Task<ConfigValidation> ValidateRemoteConfigAsync(RemoteValidateRequest request, CancellationToken cancel = default);
    Task<RemoteApplyAccepted> ApplyRemoteConfigAsync(RemoteApplyRequest request, CancellationToken cancel = default);
    Task<CommandResult> ConfirmRemoteConfigAsync(RemoteConfirmRequest request, CancellationToken cancel = default);
}

namespace Hydra.Management;

// Shared by ManagementClient (talks to the real local daemon over the versioned framed-JSON
// protocol) and MockManagementClient (returns canned data for `hydra tui --demo`), so the TUI
// itself never knows or cares which one it's talking to.
internal interface IManagementClient
{
    Task<ServerHello> Hello(CancellationToken cancel = default);
    Task<HydraStatusSnapshot> GetStatus(CancellationToken cancel = default);
    Task<ManagementLogPage> GetLogs(long after, CancellationToken cancel = default);
    Task<ConfigDocument> GetConfig(CancellationToken cancel = default);
    Task<ConfigDocument> SaveConfig(SaveConfigRequest request, CancellationToken cancel = default);
    Task<CommandResult> ReconnectRelay(CancellationToken cancel = default);
    Task<CommandResult> RestartHydra(CancellationToken cancel = default);
    Task<CommandResult> ShutdownHydra(CancellationToken cancel = default);
    Task<RemotePairResult> PairRemote(RemotePairRequest request, CancellationToken cancel = default);
    Task<RemoteConfigDocument> GetRemoteConfig(string host, CancellationToken cancel = default);
    Task<ConfigValidation> ValidateRemoteConfig(RemoteValidateRequest request, CancellationToken cancel = default);
    Task<RemoteApplyAccepted> ApplyRemoteConfig(RemoteApplyRequest request, CancellationToken cancel = default);
    Task<CommandResult> ConfirmRemoteConfig(RemoteConfirmRequest request, CancellationToken cancel = default);
}

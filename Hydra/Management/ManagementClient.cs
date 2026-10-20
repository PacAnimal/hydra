using System.IO.Pipes;
using System.Net.Sockets;

namespace Hydra.Management;

internal sealed class ManagementClient(string configPath) : IManagementClient
{
    private readonly ManagementEndpoint _endpoint = ManagementEndpoint.ForConfig(configPath);

    internal async Task<T> Invoke<T>(string method, object? payload = null, CancellationToken cancel = default)
    {
        await using var stream = await Connect(cancel);
        var json = payload == null ? null : ManagementJson.Serialize(payload);
        await ManagementFraming.Write(stream, new ManagementRequest(method, json), cancel);
        var response = await ManagementFraming.Read<ManagementResponse>(stream, cancel);
        if (!response.Success) throw new InvalidOperationException(response.Error ?? "Management request failed.");
        return ManagementJson.Deserialize<T>(response.Json);
    }

    public Task<ServerHello> Hello(CancellationToken cancel = default) => Invoke<ServerHello>(ManagementMethods.Hello, cancel: cancel);
    public Task<HydraStatusSnapshot> GetStatus(CancellationToken cancel = default) => Invoke<HydraStatusSnapshot>(ManagementMethods.Status, cancel: cancel);
    public Task<ManagementLogPage> GetLogs(long after, CancellationToken cancel = default) => Invoke<ManagementLogPage>(ManagementMethods.Logs, after, cancel);
    public Task<ConfigDocument> GetConfig(CancellationToken cancel = default) => Invoke<ConfigDocument>(ManagementMethods.ConfigGet, cancel: cancel);
    public Task<ConfigDocument> SaveConfig(SaveConfigRequest request, CancellationToken cancel = default) => Invoke<ConfigDocument>(ManagementMethods.ConfigSave, request, cancel);
    public Task<CommandResult> ReconnectRelay(CancellationToken cancel = default) => Invoke<CommandResult>(ManagementMethods.RelayReconnect, cancel: cancel);
    public Task<CommandResult> RestartHydra(CancellationToken cancel = default) => Invoke<CommandResult>(ManagementMethods.HydraRestart, cancel: cancel);
    public Task<CommandResult> ShutdownHydra(CancellationToken cancel = default) => Invoke<CommandResult>(ManagementMethods.HydraShutdown, cancel: cancel);
    public Task<RemotePairResult> PairRemote(RemotePairRequest request, CancellationToken cancel = default) => Invoke<RemotePairResult>(ManagementMethods.RemotePair, request, cancel);
    public Task<RemoteConfigDocument> GetRemoteConfig(string host, CancellationToken cancel = default) => Invoke<RemoteConfigDocument>(ManagementMethods.RemoteConfigGet, new RemoteHostRequest(host), cancel);
    public Task<ConfigValidation> ValidateRemoteConfig(RemoteValidateRequest request, CancellationToken cancel = default) => Invoke<ConfigValidation>(ManagementMethods.RemoteConfigValidate, request, cancel);
    public Task<RemoteApplyAccepted> ApplyRemoteConfig(RemoteApplyRequest request, CancellationToken cancel = default) => Invoke<RemoteApplyAccepted>(ManagementMethods.RemoteConfigApply, request, cancel);
    public Task<CommandResult> ConfirmRemoteConfig(RemoteConfirmRequest request, CancellationToken cancel = default) => Invoke<CommandResult>(ManagementMethods.RemoteConfigConfirm, request, cancel);

    private async Task<Stream> Connect(CancellationToken cancel)
    {
        if (_endpoint.IsNamedPipe)
        {
            var pipe = new NamedPipeClientStream(".", _endpoint.Address, PipeDirection.InOut, PipeOptions.Asynchronous);
            try
            {
                await pipe.ConnectAsync(cancel);
                return pipe;
            }
            catch
            {
                await pipe.DisposeAsync();
                throw;
            }
        }

        var socket = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
        try
        {
            await socket.ConnectAsync(new UnixDomainSocketEndPoint(_endpoint.Address), cancel);
            return new NetworkStream(socket, ownsSocket: true);
        }
        catch
        {
            socket.Dispose();
            throw;
        }
    }
}

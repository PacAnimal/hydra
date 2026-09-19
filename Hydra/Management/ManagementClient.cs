using System.IO.Pipes;
using System.Net.Sockets;

namespace Hydra.Management;

internal sealed class ManagementClient(string configPath) : IManagementClient
{
    private readonly ManagementEndpoint _endpoint = ManagementEndpoint.ForConfig(configPath);

    internal async Task<T> InvokeAsync<T>(string method, object? payload = null, CancellationToken cancel = default)
    {
        await using var stream = await ConnectAsync(cancel);
        var json = payload == null ? null : ManagementJson.Serialize(payload);
        await ManagementFraming.WriteAsync(stream, new ManagementRequest(method, json), cancel);
        var response = await ManagementFraming.ReadAsync<ManagementResponse>(stream, cancel);
        if (!response.Success) throw new InvalidOperationException(response.Error ?? "Management request failed.");
        return ManagementJson.Deserialize<T>(response.Json);
    }

    public Task<ServerHello> HelloAsync(CancellationToken cancel = default) => InvokeAsync<ServerHello>("hello", cancel: cancel);
    public Task<HydraStatusSnapshot> GetStatusAsync(CancellationToken cancel = default) => InvokeAsync<HydraStatusSnapshot>("status", cancel: cancel);
    public Task<ManagementLogPage> GetLogsAsync(long after, CancellationToken cancel = default) => InvokeAsync<ManagementLogPage>("logs", after, cancel);
    public Task<ConfigDocument> GetConfigAsync(CancellationToken cancel = default) => InvokeAsync<ConfigDocument>("config.get", cancel: cancel);
    public Task<ConfigValidation> ValidateConfigAsync(string json, CancellationToken cancel = default) => InvokeAsync<ConfigValidation>("config.validate", json, cancel);
    public Task<ConfigDocument> SaveConfigAsync(SaveConfigRequest request, CancellationToken cancel = default) => InvokeAsync<ConfigDocument>("config.save", request, cancel);
    public Task<CommandResult> ReconnectRelayAsync(CancellationToken cancel = default) => InvokeAsync<CommandResult>("relay.reconnect", cancel: cancel);
    public Task<CommandResult> RestartHydraAsync(CancellationToken cancel = default) => InvokeAsync<CommandResult>("hydra.restart", cancel: cancel);
    public Task<CommandResult> ShutdownHydraAsync(CancellationToken cancel = default) => InvokeAsync<CommandResult>("hydra.shutdown", cancel: cancel);
    public Task<RemotePairResult> PairRemoteAsync(RemotePairRequest request, CancellationToken cancel = default) => InvokeAsync<RemotePairResult>("remote.pair", request, cancel);
    public Task<RemoteConfigDocument> GetRemoteConfigAsync(string host, CancellationToken cancel = default) => InvokeAsync<RemoteConfigDocument>("remote.config.get", new RemoteHostRequest(host), cancel);
    public Task<ConfigValidation> ValidateRemoteConfigAsync(RemoteValidateRequest request, CancellationToken cancel = default) => InvokeAsync<ConfigValidation>("remote.config.validate", request, cancel);
    public Task<RemoteApplyAccepted> ApplyRemoteConfigAsync(RemoteApplyRequest request, CancellationToken cancel = default) => InvokeAsync<RemoteApplyAccepted>("remote.config.apply", request, cancel);
    public Task<CommandResult> ConfirmRemoteConfigAsync(RemoteConfirmRequest request, CancellationToken cancel = default) => InvokeAsync<CommandResult>("remote.config.confirm", request, cancel);

    private async Task<Stream> ConnectAsync(CancellationToken cancel)
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

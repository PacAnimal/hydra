using System.Collections.Concurrent;
using System.IO.Pipes;
using System.Net.Sockets;
using System.Reflection;
using System.Runtime.Versioning;
using Hydra.Platform;
using Hydra.Platform.Windows;
using Hydra.Relay;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.DependencyInjection;

namespace Hydra.Management;

internal sealed class ManagementServer(
    HydraRuntimeInfo runtime,
    HydraStatusService status,
    TransactionalConfigStore config,
    ManagementLogBuffer logs,
    IHydraLifetimeController lifetime,
    IServiceProvider services,
    ILogger<ManagementServer> log) : BackgroundService
{
    internal const int MaxConcurrentHandlers = 16;
    private readonly ManagementEndpoint _endpoint = ManagementEndpoint.ForConfig(runtime.ConfigPath);
    private readonly SemaphoreSlim _handlerSlots = new(MaxConcurrentHandlers, MaxConcurrentHandlers);
    private readonly ConcurrentDictionary<long, Task> _handlers = new();
    private Socket? _unixListener;
    private long _nextHandlerId;

    internal int ActiveHandlerCount => _handlers.Count;

    protected override Task ExecuteAsync(CancellationToken stoppingToken) =>
        OperatingSystem.IsWindows() ? RunNamedPipeAsync(stoppingToken) : RunUnixSocketAsync(stoppingToken);

    [SupportedOSPlatform("windows")]
    private async Task RunNamedPipeAsync(CancellationToken cancel)
    {
        try
        {
            while (!cancel.IsCancellationRequested)
            {
                await _handlerSlots.WaitAsync(cancel);
                NamedPipeServerStream? pipe = null;
                var handlerOwnsSlot = false;
                try
                {
                    pipe = CreateNamedPipe();
                    using var wait = CancellationTokenSource.CreateLinkedTokenSource(cancel);
                    if (RunMode.IsSessionChild) wait.CancelAfter(TimeSpan.FromSeconds(2));
                    try
                    {
                        await pipe.WaitForConnectionAsync(wait.Token);
                    }
                    catch (OperationCanceledException) when (!cancel.IsCancellationRequested)
                    {
                        await pipe.DisposeAsync();
                        _handlerSlots.Release();
                        continue;
                    }
                    StartHandler(pipe, cancel);
                    handlerOwnsSlot = true;
                }
                catch
                {
                    if (pipe != null) await pipe.DisposeAsync();
                    if (!handlerOwnsSlot) _handlerSlots.Release();
                    throw;
                }
            }
        }
        finally { await WaitForHandlersAsync(); }
    }

    [SupportedOSPlatform("windows")]
    private NamedPipeServerStream CreateNamedPipe()
    {
        if (!RunMode.IsSessionChild)
            return new NamedPipeServerStream(
                _endpoint.Address,
                PipeDirection.InOut,
                NamedPipeServerStream.MaxAllowedServerInstances,
                PipeTransmissionMode.Byte,
                PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly,
                ManagementProtocol.MaxMessageBytes,
                ManagementProtocol.MaxMessageBytes);

        return NamedPipeServerStreamAcl.Create(
            _endpoint.Address,
            PipeDirection.InOut,
            NamedPipeServerStream.MaxAllowedServerInstances,
            PipeTransmissionMode.Byte,
            PipeOptions.Asynchronous,
            ManagementProtocol.MaxMessageBytes,
            ManagementProtocol.MaxMessageBytes,
            Win32Session.CreateManagementPipeSecurity());
    }

    private async Task RunUnixSocketAsync(CancellationToken cancel)
    {
        if (!await _endpoint.RemoveStaleUnixSocketAsync(cancel))
        {
            log.LogWarning("Hydra management endpoint is already owned by another process");
            return;
        }
        _unixListener = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
        try
        {
            _unixListener.Bind(new UnixDomainSocketEndPoint(_endpoint.Address));
        }
        catch (SocketException ex) when (ex.SocketErrorCode == SocketError.AddressAlreadyInUse)
        {
            _unixListener.Dispose();
            _unixListener = null;
            log.LogWarning("Hydra management endpoint was claimed by another process during startup");
            return;
        }
        if (!OperatingSystem.IsWindows())
            File.SetUnixFileMode(_endpoint.Address, UnixFileMode.UserRead | UnixFileMode.UserWrite);
        _unixListener.Listen(8);
        log.LogInformation("Hydra management endpoint ready");

        try
        {
            while (!cancel.IsCancellationRequested)
            {
                await _handlerSlots.WaitAsync(cancel);
                try
                {
                    var socket = await _unixListener.AcceptAsync(cancel);
                    StartHandler(new NetworkStream(socket, ownsSocket: true), cancel);
                }
                catch
                {
                    _handlerSlots.Release();
                    throw;
                }
            }
        }
        finally
        {
            _unixListener.Dispose();
            _unixListener = null;
            await WaitForHandlersAsync();
            if (File.Exists(_endpoint.Address)) File.Delete(_endpoint.Address);
        }
    }

    private void StartHandler(Stream stream, CancellationToken serverCancel)
    {
        var id = Interlocked.Increment(ref _nextHandlerId);
        var handler = HandleAndDisposeAsync(stream, serverCancel);
        _handlers[id] = handler;
        _ = ObserveHandlerAsync(id, handler);
    }

    private async Task ObserveHandlerAsync(long id, Task handler)
    {
        try { await handler; }
        catch (Exception ex) { log.LogDebug(ex, "Management handler stopped unexpectedly"); }
        finally
        {
            _handlers.TryRemove(id, out _);
            _handlerSlots.Release();
        }
    }

    private async Task WaitForHandlersAsync()
    {
        var active = _handlers.Values.ToArray();
        if (active.Length > 0) await Task.WhenAll(active);
    }

    private async Task HandleAndDisposeAsync(Stream stream, CancellationToken serverCancel)
    {
        await using (stream)
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(serverCancel);
            timeout.CancelAfter(TimeSpan.FromSeconds(15));
            try
            {
                var request = await ManagementFraming.ReadAsync<ManagementRequest>(stream, timeout.Token);
                var response = await DispatchAsync(request, timeout.Token);
                await ManagementFraming.WriteAsync(stream, response, timeout.Token);
            }
            catch (OperationCanceledException) when (serverCancel.IsCancellationRequested) { }
            catch (Exception ex)
            {
                using var errorTimeout = CancellationTokenSource.CreateLinkedTokenSource(serverCancel);
                errorTimeout.CancelAfter(TimeSpan.FromSeconds(1));
                try { await ManagementFraming.WriteAsync(stream, ManagementResponse.Fail(ex.Message), errorTimeout.Token); }
                catch { }
                log.LogDebug(ex, "Management request failed");
            }
        }
    }

    internal async Task<ManagementResponse> DispatchAsync(ManagementRequest request, CancellationToken cancel)
    {
        switch (request.Method)
        {
            case "hello":
                var version = Assembly.GetEntryAssembly()?.GetName().Version?.ToString(3) ?? "0.0.0";
                return ManagementResponse.Ok(new ServerHello(ManagementProtocol.Version, version, _endpoint.InstanceId, Environment.ProcessId));
            case "status":
                return ManagementResponse.Ok(await status.GetAsync(cancel));
            case "logs":
                return ManagementResponse.Ok(logs.Read(ManagementJson.Deserialize<long>(request.Json)));
            case "config.get":
                return ManagementResponse.Ok(await config.ReadAsync(cancel));
            case "config.validate":
                return ManagementResponse.Ok(TransactionalConfigStore.Validate(ManagementJson.Deserialize<string>(request.Json)));
            case "config.save":
                {
                    var save = ManagementJson.Deserialize<SaveConfigRequest>(request.Json);
                    var document = await config.SaveAsync(save.ExpectedRevision, save.Json, cancel);
                    if (save.Restart) lifetime.RestartAfterResponse();
                    return ManagementResponse.Ok(document);
                }
            case "relay.reconnect":
                {
                    var relay = services.GetService(typeof(IRelaySender)) as IRelaySender;
                    var accepted = relay?.RequestReconnect() == true;
                    return ManagementResponse.Ok(new CommandResult(accepted, accepted ? "Relay reconnect requested." : "Relay is not connected."));
                }
            case "hydra.restart":
                lifetime.RestartAfterResponse();
                return ManagementResponse.Ok(new CommandResult(true, "Hydra restart requested."));
            case "hydra.shutdown":
                return ManagementResponse.Ok(lifetime.ShutdownAfterResponse());
            case "remote.pair":
                return ManagementResponse.Ok(await services.GetRequiredService<RemoteManagementService>()
                    .PairAsync(ManagementJson.Deserialize<RemotePairRequest>(request.Json), cancel));
            case "remote.config.get":
                return ManagementResponse.Ok(await services.GetRequiredService<RemoteManagementService>()
                    .GetConfigAsync(ManagementJson.Deserialize<RemoteHostRequest>(request.Json).Host, cancel));
            case "remote.config.validate":
                return ManagementResponse.Ok(await services.GetRequiredService<RemoteManagementService>()
                    .ValidateConfigAsync(ManagementJson.Deserialize<RemoteValidateRequest>(request.Json), cancel));
            case "remote.config.apply":
                return ManagementResponse.Ok(await services.GetRequiredService<RemoteManagementService>()
                    .ApplyConfigAsync(ManagementJson.Deserialize<RemoteApplyRequest>(request.Json), cancel));
            case "remote.config.confirm":
                await services.GetRequiredService<RemoteManagementService>()
                    .ConfirmConfigAsync(ManagementJson.Deserialize<RemoteConfirmRequest>(request.Json), cancel);
                return ManagementResponse.Ok(new CommandResult(true, "Remote configuration confirmed."));
            default:
                return ManagementResponse.Fail($"Unknown management method '{request.Method}'.");
        }
    }
}

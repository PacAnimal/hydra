using System.Collections.Concurrent;
using System.Text;
using Hydra.Relay;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Hydra.Management;

internal sealed class RemoteManagementService(
    IRelaySender relay,
    RemoteManagementStore store,
    TransactionalConfigStore config,
    RemoteApplyStore apply,
    IHydraLifetimeController lifetime,
    ILogger<RemoteManagementService> log) : IHostedService
{
    private readonly ConcurrentDictionary<Guid, PendingRequest> _pending = new();

    public Task StartAsync(CancellationToken cancellationToken)
    {
        relay.MessageReceived += OnMessageReceived;
        relay.Disconnected += OnDisconnected;
        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken)
    {
        relay.MessageReceived -= OnMessageReceived;
        relay.Disconnected -= OnDisconnected;
        CancelPending("Relay disconnected.");
        return Task.CompletedTask;
    }

    internal async Task<RemotePairResult> PairAsync(RemotePairRequest pair, CancellationToken cancel)
    {
        var (controllerId, controllerSecret) = await store.CreateTargetCredentialAsync(cancel);
        var payload = new RemotePairPayload(pair.PairingCode, controllerSecret);
        var response = await InvokeAsync(pair.Host, controllerId, pair.PairingCode, "pair", payload, controllerSecret, cancel);
        var result = ManagementJson.Deserialize<RemotePairResult>(response);
        if (result.Paired)
            await store.SaveTargetAsync(pair.Host, controllerSecret, cancel);
        return result;
    }

    internal async Task<RemoteConfigDocument> GetConfigAsync(string host, CancellationToken cancel)
    {
        var response = await InvokeAuthorizedAsync(host, "config.get", new { }, cancel);
        return ManagementJson.Deserialize<RemoteConfigDocument>(response) with { Host = host };
    }

    internal async Task<ConfigValidation> ValidateConfigAsync(RemoteValidateRequest request, CancellationToken cancel)
    {
        var response = await InvokeAuthorizedAsync(request.Host, "config.validate", request.Json, cancel);
        return ManagementJson.Deserialize<ConfigValidation>(response);
    }

    internal async Task<RemoteApplyAccepted> ApplyConfigAsync(RemoteApplyRequest request, CancellationToken cancel)
    {
        var response = await InvokeAuthorizedAsync(request.Host, "config.apply",
            new RemoteApplyPayload(request.ExpectedRevision, request.Json), cancel);
        return ManagementJson.Deserialize<RemoteApplyAccepted>(response);
    }

    internal async Task ConfirmConfigAsync(RemoteConfirmRequest request, CancellationToken cancel)
    {
        _ = await InvokeAuthorizedAsync(request.Host, "config.confirm",
            new RemoteConfirmPayload(request.TransactionId, request.ExpectedRevision), cancel);
    }

    private async Task<string> InvokeAuthorizedAsync<T>(string host, string operation, T payload, CancellationToken cancel)
    {
        var credential = await store.GetTargetAsync(host, cancel)
            ?? throw new InvalidOperationException($"Remote host '{host}' is not paired.");
        return await InvokeAsync(host, credential.ControllerId, credential.Secret, operation, payload, credential.Secret, cancel);
    }

    private async Task<string> InvokeAsync<T>(string host, string controllerId, string signingSecret,
        string operation, T payload, string responseSecret, CancellationToken cancel)
    {
        var request = new RemoteWireRequest(
            RemoteManagementProtocol.Version,
            Guid.NewGuid(),
            controllerId,
            DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
            RemoteManagementCrypto.RandomSecret(18),
            operation,
            ManagementJson.Serialize(payload),
            "");
        request = request with { Signature = RemoteManagementCrypto.SignRequest(request, signingSecret) };
        var completion = new TaskCompletionSource<RemoteWireResponse>(TaskCreationOptions.RunContinuationsAsynchronously);
        if (!_pending.TryAdd(request.RequestId, new PendingRequest(host, responseSecret, completion)))
            throw new InvalidOperationException("Remote request ID collision.");

        try
        {
            relay.Send([host], Encode(MessageKind.RemoteManagementRequest, request));
            var response = await completion.Task.WaitAsync(RemoteManagementProtocol.RequestTimeout, cancel);
            if (!response.Success) throw new InvalidOperationException(response.Error ?? "Remote management request failed.");
            return response.Json ?? throw new InvalidOperationException("Remote management response was empty.");
        }
        finally { _pending.TryRemove(request.RequestId, out _); }
    }

    private async Task OnMessageReceived(string sourceHost, MessageKind kind, ReadOnlyMemory<byte> body)
    {
        if (kind is not (MessageKind.RemoteManagementRequest or MessageKind.RemoteManagementResponse)) return;
        if (body.Length > RemoteManagementProtocol.MaxPayloadBytes)
        {
            log.LogWarning("Rejected oversized remote-management payload from {Host}", sourceHost);
            return;
        }

        if (kind == MessageKind.RemoteManagementResponse)
        {
            await HandleResponse(sourceHost, body);
            return;
        }
        await HandleRequest(sourceHost, body);
    }

    private Task HandleResponse(string sourceHost, ReadOnlyMemory<byte> body)
    {
        RemoteWireResponse response;
        try { response = ManagementJson.Deserialize<RemoteWireResponse>(Encoding.UTF8.GetString(body.Span)); }
        catch (Exception ex) { log.LogDebug(ex, "Invalid remote-management response from {Host}", sourceHost); return Task.CompletedTask; }

        if (!_pending.TryGetValue(response.RequestId, out var pending)
            || !pending.Host.Equals(sourceHost, StringComparison.OrdinalIgnoreCase)
            || response.Version != RemoteManagementProtocol.Version
            || !Fresh(response.TimestampUnixMs)
            || !RemoteManagementCrypto.VerifyResponse(response, pending.ResponseSecret))
            return Task.CompletedTask;
        pending.Completion.TrySetResult(response);
        return Task.CompletedTask;
    }

    private async Task HandleRequest(string sourceHost, ReadOnlyMemory<byte> body)
    {
        RemoteWireRequest request;
        try { request = ManagementJson.Deserialize<RemoteWireRequest>(Encoding.UTF8.GetString(body.Span)); }
        catch (Exception ex) { log.LogDebug(ex, "Invalid remote-management request from {Host}", sourceHost); return; }

        if (request.Version != RemoteManagementProtocol.Version || !Fresh(request.TimestampUnixMs))
            return;

        string? secret = null;
        RemotePairPayload? pair = null;
        if (request.Operation == "pair")
        {
            try { pair = ManagementJson.Deserialize<RemotePairPayload>(request.Json); }
            catch { return; }
            if (!RemoteManagementCrypto.VerifyRequest(request, pair.PairingCode))
                return;
            if (!await store.ConsumePairingCodeAsync(pair.PairingCode, CancellationToken.None))
            {
                await SendResponse(sourceHost, request.RequestId, pair.ControllerSecret, true,
                    ManagementJson.Serialize(new RemotePairResult(false, "The pairing code is invalid, expired, or already used.")), null);
                return;
            }
            secret = pair.ControllerSecret;
            await store.SaveControllerAsync(request.ControllerId, secret, CancellationToken.None);
        }
        else
        {
            secret = await store.GetControllerSecretAsync(request.ControllerId, CancellationToken.None);
            if (secret == null || !RemoteManagementCrypto.VerifyRequest(request, secret)
                || !await store.RememberRequestAsync(request.ControllerId, request.Nonce, request.TimestampUnixMs, CancellationToken.None)) return;
        }

        try
        {
            var json = request.Operation switch
            {
                "pair" => ManagementJson.Serialize(new RemotePairResult(true, $"Paired with {sourceHost}.")),
                "config.get" => ManagementJson.Serialize(await ReadMaskedConfig(sourceHost)),
                "config.validate" => ManagementJson.Serialize(await ValidateMaskedConfig(request.Json)),
                "config.apply" => ManagementJson.Serialize(await BeginApply(request.Json)),
                "config.confirm" => await ConfirmApply(request.Json),
                _ => throw new InvalidOperationException($"Unsupported remote operation '{request.Operation}'.")
            };
            await SendResponse(sourceHost, request.RequestId, secret, true, json, null);
            if (request.Operation == "config.apply") lifetime.RestartAfterResponse();
        }
        catch (Exception ex)
        {
            log.LogWarning(ex, "Remote-management operation {Operation} failed for {Host}", request.Operation, sourceHost);
            await SendResponse(sourceHost, request.RequestId, secret, false, null, ex.Message);
        }
    }

    private async Task<RemoteConfigDocument> ReadMaskedConfig(string sourceHost)
    {
        var document = await config.ReadAsync();
        return new RemoteConfigDocument(sourceHost, document.Revision, ConfigSecretMask.Mask(document.Json), await apply.GetStateAsync());
    }

    private async Task<ConfigValidation> ValidateMaskedConfig(string payloadJson)
    {
        var edited = ManagementJson.Deserialize<string>(payloadJson);
        var source = await config.ReadAsync();
        return TransactionalConfigStore.Validate(ConfigSecretMask.Restore(edited, source.Json));
    }

    private async Task<RemoteApplyAccepted> BeginApply(string payloadJson)
    {
        var payload = ManagementJson.Deserialize<RemoteApplyPayload>(payloadJson);
        return await apply.BeginAsync(payload.ExpectedRevision, payload.Json, CancellationToken.None);
    }

    private async Task<string> ConfirmApply(string payloadJson)
    {
        var payload = ManagementJson.Deserialize<RemoteConfirmPayload>(payloadJson);
        await apply.ConfirmAsync(payload.TransactionId, payload.ExpectedRevision, CancellationToken.None);
        return ManagementJson.Serialize(new CommandResult(true, "Remote configuration confirmed."));
    }

    private async Task SendResponse(string host, Guid requestId, string secret, bool success, string? json, string? error)
    {
        var response = new RemoteWireResponse(RemoteManagementProtocol.Version, requestId,
            DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(), success, json, error, "");
        response = response with { Signature = RemoteManagementCrypto.SignResponse(response, secret) };
        byte[] encoded;
        try { encoded = Encode(MessageKind.RemoteManagementResponse, response); }
        catch (InvalidOperationException) when (success)
        {
            response = response with
            {
                Success = false,
                Json = null,
                Error = "Remote management response exceeds the payload limit.",
                Signature = ""
            };
            response = response with { Signature = RemoteManagementCrypto.SignResponse(response, secret) };
            encoded = Encode(MessageKind.RemoteManagementResponse, response);
        }
        await relay.SendReliableAsync([host], encoded);
    }

    private static byte[] Encode<T>(MessageKind kind, T message)
    {
        var encoded = MessageSerializer.Encode(kind, message);
        if (encoded.Length - 1 > RemoteManagementProtocol.MaxPayloadBytes)
            throw new InvalidOperationException("Remote management payload exceeds the 1 MiB limit.");
        return encoded;
    }

    private static bool Fresh(long timestampUnixMs)
    {
        var now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        var skew = (long)RemoteManagementProtocol.ClockSkew.TotalMilliseconds;
        return timestampUnixMs >= now - skew && timestampUnixMs <= now + skew;
    }

    private Task OnDisconnected()
    {
        CancelPending("Relay disconnected.");
        return Task.CompletedTask;
    }

    private void CancelPending(string message)
    {
        foreach (var pending in _pending.Values)
            pending.Completion.TrySetException(new IOException(message));
    }

    private sealed record PendingRequest(string Host, string ResponseSecret, TaskCompletionSource<RemoteWireResponse> Completion);
}

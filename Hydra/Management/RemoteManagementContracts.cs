namespace Hydra.Management;

internal static class RemoteManagementProtocol
{
    internal const int Version = 1;
    internal const int MaxPayloadBytes = 1024 * 1024;
    internal static readonly TimeSpan ClockSkew = TimeSpan.FromMinutes(2);
    internal static readonly TimeSpan RequestTimeout = TimeSpan.FromSeconds(8);
}

public sealed record RemotePairRequest(string Host, string PairingCode);
public sealed record RemotePairResult(bool Paired, string Message);
public sealed record RemoteHostRequest(string Host);
public sealed record RemoteValidateRequest(string Host, string Json);
public sealed record RemoteApplyRequest(string Host, string ExpectedRevision, string Json);
public sealed record RemoteConfirmRequest(string Host, Guid TransactionId, string ExpectedRevision);
public sealed record RemoteConfigDocument(string Host, string Revision, string Json, RemoteApplyState? Apply);
public sealed record RemoteApplyState(Guid TransactionId, string CandidateRevision, DateTimeOffset ExpiresAt);
public sealed record RemoteApplyAccepted(Guid TransactionId, string CandidateRevision, DateTimeOffset ExpiresAt, string Message);

internal sealed record RemoteWireRequest(
    int Version,
    Guid RequestId,
    string ControllerId,
    long TimestampUnixMs,
    string Nonce,
    string Operation,
    string Json,
    string Signature);

internal sealed record RemoteWireResponse(
    int Version,
    Guid RequestId,
    long TimestampUnixMs,
    bool Success,
    string? Json,
    string? Error,
    string Signature);

internal sealed record RemotePairPayload(string PairingCode, string ControllerSecret);
internal sealed record RemoteApplyPayload(string ExpectedRevision, string Json);
internal sealed record RemoteConfirmPayload(Guid TransactionId, string ExpectedRevision);
internal sealed record RemoteApplyMarker(Guid TransactionId, string CandidateRevision, string PreviousRevision, DateTimeOffset ExpiresAt);
internal sealed record StoredRemoteTarget(string Host, string Secret);
internal sealed record StoredRemoteController(string Id, string Secret);
internal sealed record StoredPairingCode(string Hash, DateTimeOffset ExpiresAt);
internal sealed record StoredReplayNonce(string ControllerId, string Nonce, long SeenAtUnixMs);
internal sealed record RemoteManagementState(
    string ControllerId,
    List<StoredRemoteTarget> Targets,
    List<StoredRemoteController> Controllers,
    List<StoredPairingCode> PairingCodes,
    List<StoredReplayNonce> ReplayNonces);

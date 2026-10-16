namespace Styx;

public static class Constants
{
    public const string RelaySigningKeyEnvVar = "RELAY_SIGNING_KEY";
    public const string RelaySigningPassphraseEnvVar = "RELAY_SIGNING_PASSPHRASE";
    public const string RelayPasswordEnvVar = "RELAY_PASSWORD";
    public const string RevokedNetworksEnvVar = "RELAY_REVOKED_NETWORKS";
    public const string RevokedIdsEnvVar = "RELAY_REVOKED_IDS";
    public const string ParallelInvocationsEnvVar = "RELAY_PARALLEL_INVOCATIONS";
    public const string DebugMessagesEnvVar = "DEBUG_MESSAGES";
    public const string LocalOnlyEnvVar = "LOCAL_ONLY";

    // SignalR hub tuning. ClientTimeout is how long a silent (e.g. half-open, wifi-dropped) connection
    // goes undetected — kept low so held keys are released and the cursor unlocks from a dead remote
    // screen within seconds, not minutes. Must stay >= 2x KeepAlive (SignalR requirement).
    public const int KeepAliveSeconds = 5;
    public const int ClientTimeoutSeconds = 15;
    // a peer with messages waiting that takes none for this long is dropped: its pings can keep the client timeout
    // fed while its download has stalled, and every sender to it waits
    public const int PeerWriteDeadlineSeconds = 30;
    public const int MaxMessageMebiBytes = 32;
    // /relay runs a client's invocations one at a time, in the order they arrived. That is what keeps a pair of
    // peers in order (Styx.md 5.2) for a client that does not wait for each Send to complete.
    public const int MaxParallelInvocations = 1;
    // /relay/parallel promises no order. Each invocation in flight can hold a whole message, so its width times
    // MaxMessageMebiBytes is what one client, logged in or not, can pin.
    public const int DefaultParallelInvocations = 4;
    public const int ParallelInvocationsLimit = 64;
    public const int ParallelInvocationsWarnAbove = 8;

    public const string RelayPath = "/relay";
    public const string ParallelRelayPath = "/relay/parallel";

    // throttle delays
    public const int AuthThrottleSeconds = 1;
    public const int NetworkConfigThrottleSeconds = 5;
    public const int StatusThrottleSeconds = 2;
}

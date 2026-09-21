namespace Styx;

public static class Constants
{
    public const string RelayPasswordEnvVar = "RELAY_PASSWORD";
    public const string DebugMessagesEnvVar = "DEBUG_MESSAGES";
    public const string LocalOnlyEnvVar = "LOCAL_ONLY";

    // SignalR hub tuning. ClientTimeout is how long a silent (e.g. half-open, wifi-dropped) connection
    // goes undetected — kept low so held keys are released and the cursor unlocks from a dead remote
    // screen within seconds, not minutes. Must stay >= 2x KeepAlive (SignalR requirement).
    public const int KeepAliveSeconds = 5;
    public const int ClientTimeoutSeconds = 15;
    public const int MaxMessageMebiBytes = 32;
    // MUST STAY >= the number of outbound lanes a peer drains at once — see RelayConnection's lane fields,
    // which is two. TypedSignalR generates Send as InvokeCoreAsync, so a lane's invocation is not finished
    // until the hub method returns, and the hub holds its slot across the write to the target. At 1, a
    // 256 KiB chunk's invocation would occupy the only slot and the keystroke behind it would not even be
    // dispatched — head-of-line blocking rebuilt at the relay, with the client-side split still looking
    // perfectly correct and every lane test still green, because those gate on the client's own encrypt step.
    public const int MaxParallelInvocations = 4;

    // throttle delays
    public const int AuthThrottleSeconds = 1;
    public const int NetworkConfigThrottleSeconds = 5;
    public const int StatusThrottleSeconds = 2;
}

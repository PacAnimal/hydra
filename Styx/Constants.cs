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
    public const int MaxParallelInvocations = 4;

    // throttle delays
    public const int AuthThrottleSeconds = 1;
    public const int NetworkConfigThrottleSeconds = 5;
    public const int StatusThrottleSeconds = 2;
}

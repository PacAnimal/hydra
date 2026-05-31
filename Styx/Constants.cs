namespace Styx;

public static class Constants
{
    public const string RelayPasswordEnvVar = "RELAY_PASSWORD";
    public const string DebugMessagesEnvVar = "DEBUG_MESSAGES";
    public const string LocalOnlyEnvVar = "LOCAL_ONLY";

    // SignalR hub tuning
    public const int KeepAliveSeconds = 30;
    public const int ClientTimeoutSeconds = 180;
    public const int MaxMessageMebiBytes = 32;
    public const int MaxParallelInvocations = 4;

    // throttle delays
    public const int AuthThrottleSeconds = 1;
    public const int NetworkConfigThrottleSeconds = 5;
}

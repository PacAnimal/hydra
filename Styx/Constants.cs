namespace Styx;

public static class Constants
{
    public const string RelayPasswordEnvVar = "RELAY_PASSWORD";

    // SignalR hub tuning
    public const int KeepAliveSeconds = 15;
    public const int ClientTimeoutSeconds = 60;
    public const int MaxMessageMebiBytes = 32;
    public const int MaxParallelInvocations = 4;

    // throttle delays
    public const int AuthThrottleSeconds = 1;
    public const int NetworkConfigThrottleSeconds = 5;
}

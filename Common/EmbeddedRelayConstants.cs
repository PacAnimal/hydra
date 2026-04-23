namespace Common;

public static class EmbeddedRelayConstants
{
    // fixed network ID for embedded Styx instances — all peers on the same server share one network
    public static readonly Guid NetworkId = new("1337b007-dead-beef-cafe-000123456789");
}

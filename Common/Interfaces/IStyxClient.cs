namespace Common.Interfaces;

public interface IStyxClient
{
    Task Receive(string sourceHost, byte[] payload);
    Task Kicked(string reason);
    // sent on every connect/disconnect in the network; lists peers currently online, excluding the recipient
    Task Peers(string[] hostNames);
}

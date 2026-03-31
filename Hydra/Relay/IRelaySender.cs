namespace Hydra.Relay;

public interface IRelaySender
{
    bool IsConnected { get; }
    ValueTask Send(string targetHost, byte[] payload);
}

public class NullRelaySender : IRelaySender
{
    public bool IsConnected => false;
    public ValueTask Send(string targetHost, byte[] payload) => ValueTask.CompletedTask;
}

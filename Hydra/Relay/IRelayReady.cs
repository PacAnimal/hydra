namespace Hydra.Relay;

// an in-process relay the client connects to, so its first connect can wait for the listener instead of a retry
public interface IRelayReady
{
    // completes when the relay is listening; faults or cancels when it stopped or failed before that
    Task WaitForReady();
}

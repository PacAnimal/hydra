using Hydra.Management;

namespace Tests.Setup;

internal sealed class FakeLifetimeController(CommandResult? shutdownResult = null) : IHydraLifetimeController
{
    private readonly TaskCompletionSource _restartRequested = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private int _restartRequests;
    private int _shutdownRequests;

    internal int RestartRequests => Volatile.Read(ref _restartRequests);
    internal int ShutdownRequests => Volatile.Read(ref _shutdownRequests);

    // completes on the first restart request
    internal Task RestartRequested => _restartRequested.Task;

    public void RestartAfterResponse()
    {
        Interlocked.Increment(ref _restartRequests);
        _restartRequested.TrySetResult();
    }

    public CommandResult ShutdownAfterResponse()
    {
        Interlocked.Increment(ref _shutdownRequests);
        return shutdownResult ?? new CommandResult(true, "Shutdown requested.");
    }
}

using Hydra.Relay;

namespace Tests.Setup;

/// <summary>Holds the router on local input: after <see cref="Hold"/>, each input waits here until released.</summary>
public sealed class HeldActivityTracker : IActivityTracker
{
    private readonly Gate _gate = new();
    private TaskCompletionSource _held = new(TaskCreationOptions.RunContinuationsAsynchronously);

    // the first input to reach the tracker since Hold
    public Task Held => _held.Task;

    public long MsSinceLocalActivity => 0;

    public void Hold()
    {
        _held = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        _gate.Hold();
    }

    public void Release() => _gate.Release();

    public async ValueTask LocalActivity()
    {
        if (!_gate.IsHeld) return;
        _held.TrySetResult();
        await _gate.Passed();
    }

    public async ValueTask RemoteActivity(string sourcePeer) => await ValueTask.CompletedTask;

    public async ValueTask IncomingPing() => await ValueTask.CompletedTask;
}

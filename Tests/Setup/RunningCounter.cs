namespace Tests.Setup;

/// <summary>Counts how many of something run at once, and the most that ever did.</summary>
public sealed class RunningCounter
{
    private int _running;
    private int _mostRunning;

    public int MostRunning => Volatile.Read(ref _mostRunning);

    public void Enter()
    {
        var running = Interlocked.Increment(ref _running);
        for (var most = MostRunning; running > most; most = MostRunning)
            Interlocked.CompareExchange(ref _mostRunning, running, most);
    }

    public void Exit() => Interlocked.Decrement(ref _running);

    // forgets the most, keeping count of what still runs
    public void Reset() => Volatile.Write(ref _mostRunning, 0);
}

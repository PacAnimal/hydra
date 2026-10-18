namespace Tests.Setup;

/// <summary>Everything a test awaits is bounded. A test that can hang takes the run with it.</summary>
public static class TestBound
{
    public static readonly TimeSpan Bound = TimeSpan.FromSeconds(30);

    // whether the task finished within Bound, faulted or not; a bounded WaitAsync cannot tell a fault from its own timeout
    public static async Task<bool> Settles(Task task) => await Task.WhenAny(task, Task.Delay(Bound)) == task;
}

namespace Tests.Setup;

/// <summary>Everything a test awaits is bounded. A test that can hang takes the run with it.</summary>
public static class TestBound
{
    public static readonly TimeSpan Bound = TimeSpan.FromSeconds(30);
}

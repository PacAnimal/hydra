namespace Hydra.Platform.Linux;

internal static class XlibRuntime
{
    private static readonly XlibInitializationGate ThreadInitialization = new(NativeMethods.XInitThreads);

    internal static bool TryInitializeThreads() => ThreadInitialization.TryInitialize();

    internal static nint TryOpenDisplay()
    {
        return TryInitializeThreads() ? NativeMethods.XOpenDisplay(null) : nint.Zero;
    }

    internal static nint OpenDisplay()
    {
        EnsureThreadsInitialized();
        return NativeMethods.XOpenDisplay(null);
    }

    internal static void EnsureThreadsInitialized()
    {
        if (!TryInitializeThreads())
            throw new InvalidOperationException("XInitThreads failed; X11 cannot be used safely from Hydra's worker threads");
    }
}

internal sealed class XlibInitializationGate(Func<int> initialize)
{
    private readonly Lazy<bool> _initialized = new(() => initialize() != 0,
        LazyThreadSafetyMode.ExecutionAndPublication);

    internal bool TryInitialize() => _initialized.Value;
}

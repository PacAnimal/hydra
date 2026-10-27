using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace Hydra.Platform.Linux;

internal static class XlibRuntime
{
    private static readonly XlibInitializationGate ThreadInitialization = new(() =>
    {
        var initialized = NativeMethods.XInitThreads();
        if (initialized != 0) XErrors.Install();
        return initialized;
    });

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

    internal static nint OpenDisplayOrThrow(string purpose)
    {
        var display = OpenDisplay();
        return display != nint.Zero
            ? display
            : throw new InvalidOperationException($"Failed to open the X11 display for {purpose} — is DISPLAY set?");
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

// Xlib's default error handler exits the process on any X error, such as a window that closed between being listed and
// being asked about, or a key another client already grabs; Hydra's counts it on the thread that received the reply,
// and carries on
internal static class XErrors
{
    [ThreadStatic] private static int _errors;

    internal static unsafe void Install() => _ = NativeMethods.XSetErrorHandler((nint)(delegate* unmanaged[Cdecl]<nint, nint, int>)&OnError);

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static int OnError(nint display, nint errorEvent)
    {
        _errors++;
        return 0;
    }

    // whether the requests made sends all succeeded, once XSync has had the server answer them
    internal static bool Succeeds(nint display, Action requests)
    {
        var before = _errors;
        requests();
        _ = NativeMethods.XSync(display, false);
        return _errors == before;
    }
}

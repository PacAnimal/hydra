using System.Runtime.Versioning;
using Cathedral.Utils;

namespace Hydra.Platform.Windows;

// manages a single background STA thread running a Win32 GetMessage loop.
// the caller provides init logic (run before ready is signalled) and optional
// cleanup logic (run after the message loop exits). custom thread messages
// (hwnd == 0) can be intercepted by supplying an onThreadMessage handler.
[SupportedOSPlatform("windows")]
internal sealed class StaMessageLoop : IDisposable
{
    private const int InitReadyTimeoutMs = 5_000;  // how long to wait for init() to complete before proceeding
    private const int ThreadJoinTimeoutMs = 3_000; // how long to wait for the STA thread to exit on Dispose

    public uint ThreadId { get; private set; }

    private readonly Thread _thread;
    private readonly Toggle _disposed = new();

    // onThreadMessage: receives thread messages (posted with hwnd=0); return true to consume, false to dispatch normally
    public StaMessageLoop(string name, Action init, Func<MSG, bool>? onThreadMessage = null, Action? onExit = null)
    {
        var ready = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        _thread = new Thread(() =>
        {
            ThreadId = NativeMethods.GetCurrentThreadId();
            try { init(); }
            catch (Exception ex) { ready.SetException(ex); return; }
            ready.SetResult();

            while (NativeMethods.GetMessage(out var msg, nint.Zero, 0, 0) > 0)
            {
                if (msg.hwnd == nint.Zero && onThreadMessage != null && onThreadMessage(msg))
                    continue;
                NativeMethods.TranslateMessage(in msg);
                NativeMethods.DispatchMessage(in msg);
            }

            onExit?.Invoke();
        })
        { IsBackground = true, Name = name };

        _thread.SetApartmentState(ApartmentState.STA);
        _thread.Start();
        // propagate init() exceptions; timeout means the thread is alive but not yet ready (unlikely)
        if (ready.Task.Wait(TimeSpan.FromMilliseconds(InitReadyTimeoutMs)))
            ready.Task.GetAwaiter().GetResult(); // rethrow if init() threw
        else
            Console.Error.WriteLine($"[WARN] STA thread '{name}' init did not complete within {InitReadyTimeoutMs}ms — proceeding anyway");
    }

    public void Dispose()
    {
        if (!_disposed.TrySet()) return;
        if (ThreadId != 0)
            NativeMethods.PostThreadMessage(ThreadId, NativeMethods.WM_QUIT, nint.Zero, nint.Zero);
        _thread.Join(TimeSpan.FromMilliseconds(ThreadJoinTimeoutMs));
    }
}

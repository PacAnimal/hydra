using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using Cathedral.Utils;

namespace Hydra.Platform.Windows;

// manages a single background STA thread running a Win32 GetMessage loop.
// the caller provides init logic (run before ready is signalled) and optional
// cleanup logic (run after the message loop exits); Post runs an action on the thread.
[SupportedOSPlatform("windows")]
internal sealed class StaMessageLoop : IDisposable
{
    private const int InitReadyTimeoutMs = 5_000;  // how long to wait for init() to complete before proceeding
    private const int ThreadJoinTimeoutMs = 3_000; // how long to wait for the STA thread to exit on Dispose
    private const uint WmRunAction = NativeMethods.WM_USER + 50; // lParam = GCHandle of the posted Action

    public uint ThreadId { get; private set; }

    private readonly Thread _thread;
    private readonly Toggle _disposed = new();

    public StaMessageLoop(string name, Action init, Action? onExit = null)
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
                if (msg.hwnd == nint.Zero && msg.message == WmRunAction)
                {
                    Run(msg.lParam);
                    continue;
                }
                NativeMethods.TranslateMessage(in msg);
                NativeMethods.DispatchMessage(in msg);
            }

            onExit?.Invoke();

            // actions posted after WM_QUIT never run, but their handles still need freeing (-1: thread messages only)
            while (NativeMethods.PeekMessage(out var left, -1, WmRunAction, WmRunAction, NativeMethods.PM_REMOVE))
                GCHandle.FromIntPtr(left.lParam).Free();
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

    // queued behind every message already posted; not run once the loop has quit
    public void Post(Action action)
    {
        var handle = GCHandle.Alloc(action);
        if (!NativeMethods.PostThreadMessage(ThreadId, WmRunAction, nint.Zero, GCHandle.ToIntPtr(handle)))
            handle.Free();
    }

    private static void Run(nint lParam)
    {
        var handle = GCHandle.FromIntPtr(lParam);
        var action = (Action)handle.Target!;
        handle.Free();
        action();
    }

    public void Dispose()
    {
        if (!_disposed.TrySet()) return;
        if (ThreadId != 0)
            NativeMethods.PostThreadMessage(ThreadId, NativeMethods.WM_QUIT, nint.Zero, nint.Zero);
        _thread.Join(TimeSpan.FromMilliseconds(ThreadJoinTimeoutMs));
    }
}

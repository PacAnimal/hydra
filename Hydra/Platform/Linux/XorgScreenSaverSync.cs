namespace Hydra.Platform.Linux;

public sealed class XorgScreenSaverSync : IScreenSaverSync, IDisposable
{
    private readonly nint _display;
    private readonly nint _rootWindow;
    private readonly bool _hasSs;    // XScreenSaver extension available
    private readonly bool _hasDpms;  // DPMS extension available
    private readonly nint _ssInfo;   // heap-allocated XScreenSaverInfo*

    private CancellationTokenSource? _watchCts;

    public XorgScreenSaverSync()
    {
        _ = NativeMethods.XInitThreads();
        _display = NativeMethods.XOpenDisplay(null);
        if (_display == nint.Zero) return;

        _rootWindow = NativeMethods.XDefaultRootWindow(_display);
        _hasSs = NativeMethods.XScreenSaverQueryExtension(_display, out _, out _);
        _hasDpms = NativeMethods.DPMSQueryExtension(_display, out _, out _);

        if (_hasSs)
            _ssInfo = NativeMethods.XScreenSaverAllocInfo();
    }

    public void StartWatching(Action onActivated, Action onDeactivated)
    {
        if (_display == nint.Zero) return;
        _watchCts = new CancellationTokenSource();
        var ct = _watchCts.Token;
        _ = Task.Run(async () => await PollAsync(onActivated, onDeactivated, ct), ct);
    }

    public void StopWatching()
    {
        _watchCts?.Cancel();
        _watchCts?.Dispose();
        _watchCts = null;
    }

    public void Activate()
    {
        if (_display == nint.Zero) return;
        _ = NativeMethods.XForceScreenSaver(_display, NativeMethods.ScreenSaverActive);
        if (_hasDpms) _ = NativeMethods.DPMSForceLevel(_display, NativeMethods.DPMSModeStandby);
        _ = NativeMethods.XFlush(_display);
    }

    public void Deactivate()
    {
        if (_display == nint.Zero) return;
        _ = NativeMethods.XForceScreenSaver(_display, NativeMethods.ScreenSaverReset);
        if (_hasDpms) _ = NativeMethods.DPMSForceLevel(_display, NativeMethods.DPMSModeOn);
        _ = NativeMethods.XFlush(_display);
    }

    private async Task PollAsync(Action onActivated, Action onDeactivated, CancellationToken ct)
    {
        var wasOn = false;
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(1));
        try
        {
            while (await timer.WaitForNextTickAsync(ct))
            {
                var isOn = IsScreensaverOn();
                if (isOn && !wasOn) onActivated();
                else if (!isOn && wasOn) onDeactivated();
                wasOn = isOn;
            }
        }
        catch (OperationCanceledException) { }
    }

    public void Suppress()
    {
        if (_display == nint.Zero) return;
        _ = NativeMethods.XResetScreenSaver(_display);
        if (_hasDpms) _ = NativeMethods.DPMSForceLevel(_display, NativeMethods.DPMSModeOn);
        _ = NativeMethods.XFlush(_display);
    }

    public void Restore() { }

    private bool IsScreensaverOn()
    {
        if (_hasSs && _ssInfo != nint.Zero)
        {
            var result = NativeMethods.XScreenSaverQueryInfo(_display, _rootWindow, _ssInfo);
            if (result != 0)
            {
                // XScreenSaverInfo.state is the first int after the Window field (offset 8 on 64-bit)
                var state = System.Runtime.InteropServices.Marshal.ReadInt32(_ssInfo, 8);
                return state == NativeMethods.ScreenSaverOn;
            }
        }

        if (_hasDpms)
        {
            NativeMethods.DPMSInfo(_display, out var level, out _);
            return level != NativeMethods.DPMSModeOn;
        }

        return false;
    }

    public void Dispose()
    {
        StopWatching();
        if (_ssInfo != nint.Zero) _ = NativeMethods.XFree(_ssInfo);
        if (_display != nint.Zero) _ = NativeMethods.XCloseDisplay(_display);
    }
}

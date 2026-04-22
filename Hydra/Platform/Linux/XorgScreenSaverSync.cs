namespace Hydra.Platform.Linux;

public sealed class XorgScreenSaverSync : PollingScreenSaverSync, IDisposable
{
    private readonly nint _display;
    private readonly nint _rootWindow;
    private readonly bool _hasSs;    // XScreenSaver extension available
    private readonly bool _hasDpms;  // DPMS extension available
    private readonly nint _ssInfo;   // heap-allocated XScreenSaverInfo*

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

    public override void StartWatching(Action onActivated, Action onDeactivated)
    {
        if (_display == nint.Zero) return;
        base.StartWatching(onActivated, onDeactivated);
    }

    public override void Activate()
    {
        if (_display == nint.Zero) return;
        _ = NativeMethods.XForceScreenSaver(_display, NativeMethods.ScreenSaverActive);
        if (_hasDpms) _ = NativeMethods.DPMSForceLevel(_display, NativeMethods.DPMSModeStandby);
        _ = NativeMethods.XFlush(_display);
    }

    public override void Deactivate()
    {
        if (_display == nint.Zero) return;
        _ = NativeMethods.XForceScreenSaver(_display, NativeMethods.ScreenSaverReset);
        if (_hasDpms) _ = NativeMethods.DPMSForceLevel(_display, NativeMethods.DPMSModeOn);
        _ = NativeMethods.XFlush(_display);
    }

    public override void Suppress()
    {
        if (_display == nint.Zero) return;
        _ = NativeMethods.XResetScreenSaver(_display);
        if (_hasDpms) _ = NativeMethods.DPMSForceLevel(_display, NativeMethods.DPMSModeOn);
        _ = NativeMethods.XFlush(_display);
    }

    public override void Restore() { }

    protected override bool IsScreensaverOn()
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

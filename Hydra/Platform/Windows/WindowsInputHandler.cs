using System.ComponentModel;
using System.Runtime.Versioning;
using Cathedral.Extensions;
using Cathedral.Utils;
using Hydra.Config;
using Hydra.Keyboard;
using Hydra.Mouse;
using Hydra.Screen;
using Microsoft.Extensions.Logging;

namespace Hydra.Platform.Windows;

public sealed class WindowsInputHandler(ILogger<WindowsInputHandler> log, IHydraProfile profile) : IPlatformInput
{
    // stored as fields to prevent GC collection while hooks are active
    private HookProc? _mouseHookProc;
    private HookProc? _keyboardHookProc;
    private nint _mouseHook;
    private nint _keyboardHook;
    private StaMessageLoop? _hookLoop;
    private readonly WinKeyResolver _keyResolver = new();
    private readonly KeyFates _keyFates = new(strayUpsPass: false);
    // never cleared: a record a lost up left is overwritten by that button's next down
    private readonly KeyFates _buttonFates = new(strayUpsPass: false);
    private Action<double, double>? _onMouseMove;
    private Action<double, double>? _onMouseDelta;
    private Action<KeyEvent>? _onKeyEvent;
    private Action<MouseButtonEvent>? _onMouseButton;
    private Action<MouseScrollEvent>? _onMouseScroll;
    private readonly WindowsShieldWindow _shield = new();
    // The point a visit recentres onto every sample (WarpCursor's last target, kept clear of the screen edges), and
    // the delta reference the next sample is measured from. The hook thread writes them every sample and the router
    // at every warp, so they are only touched under _warpLock, through the helpers below.
    private ScreenPoint _warpTarget = new(-1, -1);
    private ScreenPoint _lastWarp = new(-1, -1);
    private readonly Lock _warpLock = new();
    private readonly Toggle _isOnVirtualScreen = new();
    // under _warpLock, see TakeStraddle and IsPreFlipParkEcho
    private readonly List<ScreenPoint> _retargetedFrom = [];
    private int _echoes;
    private ScreenPoint? _echoGuard;
    private int _echoGuardTick;
    private const int MaxEchoes = 2;

    // the hook's input timestamps (MSLLHOOKSTRUCT.time) are on this clock
    internal Func<int> TickCount { get; init; } = static () => Environment.TickCount;

    // whether the OS holds a virtual key down. read in a hook callback it is the state BEFORE that key's own event,
    // and a swallowed key never changes it, so while virtual a key it holds was pressed while local
    internal Func<int, bool> IsKeyDown { get; init; } = static vk => (NativeMethods.GetAsyncKeyState(vk) & 0x8000) != 0;

    public bool IsOnVirtualScreen
    {
        get => _isOnVirtualScreen;
        set
        {
            // marked here, as the flag flips, so a bounce between two hook samples is never missed
            lock (_warpLock) FlipLocked(value);
        }
    }

    private void FlipLocked(bool value)
    {
        if (!_isOnVirtualScreen.TrySet(value)) return;
        if (value)
        {
            // the first sample of a visit may still have been measured on the local screen
            MarkRetargetLocked(_warpTarget);
            _echoGuard = null;
        }
        else
        {
            // the return's warp marks its own retarget
            _retargetedFrom.Clear();
            _echoGuard = _warpTarget;
            _echoGuardTick = TickCount();
        }
    }

    private Timer? _healthTimer;
    // Windows 7+ silently removes a low-level hook whose callback overruns LowLevelHooksTimeout (a GC pause, a loaded
    // box) and says nothing. A live hook sees its input all the time, so a hook quiet for HookIdleMs is reinstalled,
    // and again every HookIdleMs it stays quiet: microseconds if it was fine, its input back if it was gone. The
    // resolver's key state is left alone: a key-up a dead hook lost cannot be told from a key held without repeating,
    // and pressing the key again releases it. Only where keys went is forgotten, so a lost up swallows no later one.
    private const int HookIdleMs = 5000;
    private int _lastMouseCallbackTick; // hook thread only
    private int _lastKeyboardCallbackTick; // hook thread only

    public void WarpCursor(int x, int y)
    {
        SetWarpState(x, y);
        NativeMethods.SetCursorPos(x, y);
    }

    public void ReturnTo(ScreenPoint? warpTo)
    {
        SetReturnState(warpTo);
        if (warpTo is { } to) NativeMethods.SetCursorPos(to.X, to.Y);
    }

    // every warp the router makes but a return's (entry park, mid-visit park); SetCursorPos stays outside the lock
    internal void SetWarpState(int x, int y)
    {
        lock (_warpLock) SetWarpStateLocked(new ScreenPoint(x, y));
    }

    // The return flips the flag and takes its warp in one step, so a sample that reads local also sees the park point
    // marked and the return as the last warp: a park echo is swallowed, a move computed at the park re-based.
    internal void SetReturnState(ScreenPoint? warpTo)
    {
        lock (_warpLock)
        {
            FlipLocked(false);
            if (warpTo is { } to) SetWarpStateLocked(to);
        }
    }

    private void SetWarpStateLocked(ScreenPoint to)
    {
        if (to != _warpTarget) MarkRetargetLocked(_warpTarget);
        _warpTarget = _lastWarp = to;
    }

    // every point the cursor left since the last handled sample: an in-flight sample or echo may come from any of them
    private void MarkRetargetLocked(ScreenPoint from)
    {
        if (!_retargetedFrom.Contains(from)) _retargetedFrom.Add(from);
        _echoes = 0;
    }

    internal ScreenPoint GetLastWarp()
    {
        lock (_warpLock) return _lastWarp;
    }

    // the hook's per-sample recentre: the delta reference becomes the target it returns
    internal ScreenPoint RecentreToTarget()
    {
        lock (_warpLock) return _lastWarp = _warpTarget;
    }

    // Warps to the target until it holds still across the warp. A router exit that retargets while
    // our SetCursorPos is in flight can land before it, and our stale park point would then leave
    // the cursor mid-screen; re-reading after each warp means ours is never the last stale one.
    internal ScreenPoint RecentreCursor(Action<int, int> setCursorPos)
    {
        var target = RecentreToTarget();
        while (true)
        {
            setCursorPos(target.X, target.Y);
            var now = RecentreToTarget();
            if (now == target) return target;
            target = now;
        }
    }

    // Windows fixes a sample's position before the hook runs, so the first one handled after the cursor is retargeted
    // (a warp, or entering a visit) may have been computed from where it was. It is re-based onto the target, once:
    // in a visit its delta is dropped, on the local screen its position. Up to two events at exactly a point it left
    // are late echoes of our own warps, swallowed without using up the re-base. A return's warp is marked as the flag
    // clears (SetReturnState). With no retarget (a return that does not warp, as an unlock or a stop; a warp to where the
    // cursor already is) samples are genuine.
    internal Straddle TakeStraddle(int x, int y)
    {
        lock (_warpLock)
        {
            if (_retargetedFrom.Count == 0) return Straddle.None;
            if (_retargetedFrom.Contains(new ScreenPoint(x, y)) && _echoes < MaxEchoes)
            {
                _echoes++;
                return Straddle.Echo;
            }
            _retargetedFrom.Clear();
            return Straddle.Rebase;
        }
    }

    // An event at the park point stamped no later than the flip is an echo of a warp made while virtual, whatever
    // order Windows delivers echoes in. The first move stamped after the flip ends the guard.
    private bool IsPreFlipParkEcho(int x, int y, uint time)
    {
        lock (_warpLock)
        {
            if (_echoGuard is not { } park) return false;
            if (unchecked((int)(time - (uint)_echoGuardTick)) > 0)
            {
                _echoGuard = null;
                return false;
            }
            return x == park.X && y == park.Y;
        }
    }

    private void Rebase(Action<int, int> setCursorPos)
    {
        var settled = RecentreCursor(setCursorPos);
        // the router wants a position only on the local screen
        if (!IsOnVirtualScreen) _onMouseMove?.Invoke(settled.X, settled.Y);
        if (log.IsEnabled(LogLevel.Debug))
            log.LogDebug("Re-based a mouse move computed before the warp onto ({X}, {Y})", settled.X, settled.Y);
    }

    public ScreenPoint? GetCursorPosition() => WindowsDisplayHelper.CursorPosition();

    [SupportedOSPlatform("windows")]
    public ValueTask HideCursor()
    {
        // hide cursor immediately — fast counter op, safe inside hook callback
        _shield.HideCursorNow();
        // window management (SetWindowPos, SetForegroundWindow) is slow; post to hook thread
        // so it runs outside the hook callback and doesn't trigger the LL hook timeout
        _hookLoop?.Post(_shield.Show);
        return ValueTask.CompletedTask;
    }

    [SupportedOSPlatform("windows")]
    public ValueTask ShowCursor()
    {
        // restore cursor synchronously, same as HideCursor hides it — then post shield teardown
        _shield.ShowCursorNow();
        _hookLoop?.Post(_shield.Hide);
        return ValueTask.CompletedTask;
    }

    internal void Subscribe(
        Action<double, double> onMouseMove,
        Action<double, double>? onMouseDelta,
        Action<KeyEvent> onKeyEvent,
        Action<MouseButtonEvent> onMouseButton,
        Action<MouseScrollEvent> onMouseScroll)
    {
        _onMouseMove = onMouseMove;
        _onMouseDelta = onMouseDelta;
        _onKeyEvent = onKeyEvent;
        _onMouseButton = onMouseButton;
        _onMouseScroll = onMouseScroll;
    }

    [SupportedOSPlatform("windows")]
    public async Task StartEventTap(
        Action<double, double> onMouseMove,
        Action<double, double>? onMouseDelta,
        Action<KeyEvent> onKeyEvent,
        Action<MouseButtonEvent> onMouseButton,
        Action<MouseScrollEvent> onMouseScroll,
        Action? onLocalActivity = null)
    {
        Subscribe(onMouseMove, onMouseDelta, onKeyEvent, onMouseButton, onMouseScroll);

        // callbacks stored as fields to prevent GC collection while hooks are active
        _mouseHookProc = MouseHookCallback;
        _keyboardHookProc = KeyboardHookCallback;

        // InstallHooks runs on the loop's thread before this returns; the hooks fire during its GetMessage
        var loop = _hookLoop = new StaMessageLoop("HydraHookPump", init: InstallHooks, onExit: () =>
        {
            _shield.Destroy();
            UninstallHook(ref _mouseHook);
            UninstallHook(ref _keyboardHook);
        });
        _healthTimer = new Timer(_ => loop.Post(ReinstallHooksIfIdle), null, 200, 200);
        await Task.CompletedTask;
    }

    // hook thread; a hook that failed to go in is retried by the health check, as one gone quiet is
    private void InstallHooks()
    {
        _lastMouseCallbackTick = _lastKeyboardCallbackTick = TickCount();
        _mouseHook = InstallHook(NativeMethods.WH_MOUSE_LL, _mouseHookProc!);
        _keyboardHook = InstallHook(NativeMethods.WH_KEYBOARD_LL, _keyboardHookProc!);
        if (_mouseHook == nint.Zero || _keyboardHook == nint.Zero)
            log.LogError("SetWindowsHookEx failed -- could not install input hooks");

        WarnIfNotOnInputDesktop(NativeMethods.GetThreadDesktop(NativeMethods.GetCurrentThreadId()));
        try { _shield.Create(profile.DebugShield); }
        catch (Win32Exception ex) { log.LogWarning(ex, "Could not create the shield window"); }
    }

    [SupportedOSPlatform("windows")]
    public void StopEventTap()
    {
        _healthTimer?.Dispose();
        _healthTimer = null;
        _hookLoop?.Dispose();
        _hookLoop = null;
    }

    public bool RecentresItself => true;

    // the async key state never sees a button our hook swallowed
    public bool SeesSwallowed(MouseButton button) => false;

    // the async state, since the router asks from a thread that reads no input of its own (GetKeyState would always say
    // up there)
    public bool AnyMouseButtonHeld() =>
        IsKeyDown(WinVirtualKey.LButton) || IsKeyDown(WinVirtualKey.RButton) || IsKeyDown(WinVirtualKey.MButton)
        || IsKeyDown(WinVirtualKey.XButton1) || IsKeyDown(WinVirtualKey.XButton2);

    // GetAsyncKeyState reads the physical button, and nothing documents whether the hook's message has had
    // SwapMouseButton applied, so the primary and secondary each count either as held
    private bool OsHolds(MouseButton button) => button switch
    {
        MouseButton.Left or MouseButton.Right => IsKeyDown(WinVirtualKey.LButton) || IsKeyDown(WinVirtualKey.RButton),
        MouseButton.Middle => IsKeyDown(WinVirtualKey.MButton),
        MouseButton.Extra1 => IsKeyDown(WinVirtualKey.XButton1),
        _ => IsKeyDown(WinVirtualKey.XButton2),
    };

    [SupportedOSPlatform("windows")]
    public ValueTask DisposeAsync() { StopEventTap(); return ValueTask.CompletedTask; }

    // SetWindowsHookEx is desktop-scoped, and it reports success whichever desktop we are on. Started
    // outside the interactive session -- Task Scheduler, a service without CreateProcessAsUser, a
    // sandboxed launcher -- Hydra therefore logs screens, relay and peers exactly as usual and simply
    // receives no input, because the cursor is moving on a desktop our hooks do not cover.
    private void WarnIfNotOnInputDesktop(nint ourDesktop)
    {
        var ours = WindowsDesktop.Name(ourDesktop);
        var input = WindowsDesktop.InputDesktopName();
        if (ours.Length == 0 || input.Length == 0 || ours.EqualsIgnoreCase(input)) return;

        log.LogWarning(
            "Input hooks are installed on desktop '{Ours}' but input is going to '{Input}' -- no keyboard or mouse " +
            "events will arrive. Run Hydra as a Windows service (--service), which launches the session child on the " +
            "interactive desktop, or start it from an interactive logon session.",
            ours, input);
    }

    // hook thread; pass null for hMod — LL hooks don't need a module handle (matches all reference KVM projects)
    private static nint InstallHook(int idHook, HookProc proc) => NativeMethods.SetWindowsHookExW(idHook, proc, nint.Zero, 0);

    private static void UninstallHook(ref nint hook)
    {
        if (hook != nint.Zero) NativeMethods.UnhookWindowsHookEx(hook);
        hook = nint.Zero;
    }

    // hook thread, see HookIdleMs
    private void ReinstallHooksIfIdle()
    {
        var quiet = TakeQuietHooks();
        if (quiet.HasFlag(Hooks.Mouse)) Reinstall(ref _mouseHook, NativeMethods.WH_MOUSE_LL, _mouseHookProc!);
        if (quiet.HasFlag(Hooks.Keyboard)) Reinstall(ref _keyboardHook, NativeMethods.WH_KEYBOARD_LL, _keyboardHookProc!);
    }

    // the new hook goes in before the old comes out, so no input slips past both
    private void Reinstall(ref nint hook, int idHook, HookProc proc)
    {
        var fresh = InstallHook(idHook, proc);
        if (fresh == nint.Zero)
        {
            log.LogWarning("Reinstalling the input hook {Hook} failed", idHook);
            return;
        }
        UninstallHook(ref hook);
        hook = fresh;
    }

    // Which hooks have been quiet for HookIdleMs, counting the reinstall answered here as a callback. During a visit
    // a mouse hook that was gone let the cursor drift off the park point, so its next sample is a retarget.
    internal Hooks TakeQuietHooks()
    {
        var now = TickCount();
        var quiet = Hooks.None;
        if (unchecked(now - _lastMouseCallbackTick) >= HookIdleMs)
        {
            _lastMouseCallbackTick = now;
            quiet |= Hooks.Mouse;
            lock (_warpLock)
                if (_isOnVirtualScreen) MarkRetargetLocked(_warpTarget);
        }
        if (unchecked(now - _lastKeyboardCallbackTick) >= HookIdleMs)
        {
            _lastKeyboardCallbackTick = now;
            quiet |= Hooks.Keyboard;
            _keyFates.Clear();
        }
        return quiet;
    }

    // The router can cross screens while the hook is handling a move, and a move passed through lands at
    // its own position over the router's warp: mid-screen on an exit, the edge on an entry. So a move is
    // swallowed if the flag said virtual either when it was handled or now. Keys, buttons and the wheel
    // follow the final read alone, since the router decides whether to forward them by its state when it
    // processes them, which the final read is closest to; a key's or button's up then goes where its press went.
    private bool SwallowsMove(bool handledAsVirtual) => handledAsVirtual || IsOnVirtualScreen;

    // nCode < 0 must go straight down the chain; the struct is read directly rather than boxed by PtrToStructure
    private unsafe nint MouseHookCallback(int nCode, nint wParam, nint lParam) =>
        SwallowOrChain(nCode >= 0 && HandleMouse((int)wParam, *(MSLLHOOKSTRUCT*)lParam, static (x, y) => NativeMethods.SetCursorPos(x, y)),
            nCode, wParam, lParam);

    private unsafe nint KeyboardHookCallback(int nCode, nint wParam, nint lParam) =>
        SwallowOrChain(nCode >= 0 && HandleKey((int)wParam, *(KBDLLHOOKSTRUCT*)lParam), nCode, wParam, lParam);

    private static nint SwallowOrChain(bool swallow, int nCode, nint wParam, nint lParam) =>
        swallow ? 1 : NativeMethods.CallNextHookEx(nint.Zero, nCode, wParam, lParam);

    // returns whether to swallow the sample
    internal bool HandleMouse(int msg, MSLLHOOKSTRUCT info, Action<int, int> setCursorPos)
    {
        _lastMouseCallbackTick = TickCount();
        if (msg == NativeMethods.WM_MOUSEMOVE)
        {
            var onVirtualScreen = IsOnVirtualScreen;
            var lastWarp = GetLastWarp();

            // ignore the echo of our own last warp or recentre
            if (info.pt.x == lastWarp.X && info.pt.y == lastWarp.Y)
                return SwallowsMove(onVirtualScreen);

            if (onVirtualScreen)
            {
                // The delta is measured on this thread from where the last recentre or warp put the cursor, immune to
                // the coalescing an absolute position goes through upstream. A sample straddling a retarget measures
                // nothing real, so it reports nothing.
                switch (TakeStraddle(info.pt.x, info.pt.y))
                {
                    case Straddle.None:
                        _onMouseDelta?.Invoke(info.pt.x - lastWarp.X, info.pt.y - lastWarp.Y);
                        // recentre per raw sample, see RecentresItself
                        RecentreCursor(setCursorPos);
                        break;
                    case Straddle.Rebase:
                        Rebase(setCursorPos);
                        break;
                }
            }
            else
            {
                if (IsPreFlipParkEcho(info.pt.x, info.pt.y, info.time)) return true;
                var straddle = TakeStraddle(info.pt.x, info.pt.y);
                if (straddle == Straddle.Rebase) Rebase(setCursorPos);
                if (straddle != Straddle.None) return true;
                _onMouseMove?.Invoke(info.pt.x, info.pt.y);
            }
            return SwallowsMove(onVirtualScreen);
        }

        var isButton = msg is NativeMethods.WM_LBUTTONDOWN or NativeMethods.WM_LBUTTONUP
            or NativeMethods.WM_RBUTTONDOWN or NativeMethods.WM_RBUTTONUP
            or NativeMethods.WM_MBUTTONDOWN or NativeMethods.WM_MBUTTONUP
            or NativeMethods.WM_XBUTTONDOWN or NativeMethods.WM_XBUTTONUP;
        var isWheel = msg is NativeMethods.WM_MOUSEWHEEL or NativeMethods.WM_MOUSEHWHEEL;

        // a click or scroll proves a deferred left Ctrl was no AltGr, so Ctrl+click and Ctrl+wheel keep their Ctrl
        if (isButton || isWheel) HandOver(_keyResolver.FlushDeferredLCtrl());

        if (isButton)
        {
            var isDown = msg is NativeMethods.WM_LBUTTONDOWN or NativeMethods.WM_RBUTTONDOWN
                or NativeMethods.WM_MBUTTONDOWN or NativeMethods.WM_XBUTTONDOWN;
            var button = msg is NativeMethods.WM_XBUTTONDOWN or NativeMethods.WM_XBUTTONUP
                ? (((info.mouseData >> 16) & 0xFFFF) == NativeMethods.XBUTTON1 ? MouseButton.Extra1 : MouseButton.Extra2)
                : msg is NativeMethods.WM_RBUTTONDOWN or NativeMethods.WM_RBUTTONUP ? MouseButton.Right
                : msg is NativeMethods.WM_MBUTTONDOWN or NativeMethods.WM_MBUTTONUP ? MouseButton.Middle
                : MouseButton.Left;
            _onMouseButton?.Invoke(new MouseButtonEvent(button, isDown));
            // a button's up goes where its down went, decided by the final read (see SwallowsMove)
            return _buttonFates.Swallow((int)button, isUp: !isDown, repeat: false, OsHolds(button), IsOnVirtualScreen);
        }

        if (isWheel)
        {
            var delta = (short)(info.mouseData >> 16);
            var scroll = msg == NativeMethods.WM_MOUSEWHEEL
                ? new MouseScrollEvent(0, delta)
                : new MouseScrollEvent(delta, 0);
            _onMouseScroll?.Invoke(scroll);
        }

        // the wheel follows the final read, see SwallowsMove
        return IsOnVirtualScreen;
    }

    // returns whether to swallow the key, which goes where its press went (KeyFates)
    internal bool HandleKey(int msg, KBDLLHOOKSTRUCT info)
    {
        _lastKeyboardCallbackTick = TickCount();
        // always resolve to track modifier state even on the real screen
        var keyEvents = _keyResolver.Resolve(msg, info);
        if (keyEvents is not null)
            foreach (var keyEvent in keyEvents)
                HandOver(keyEvent);
        return _keyFates.Swallow(WinKeyResolver.TrackingVk(info), WinKeyResolver.IsKeyUp(msg), repeat: null,
            osHolds: OsHoldsKey(info.vkCode), IsOnVirtualScreen);
    }

    // both Enter keys are VK_RETURN to the OS, so its state says nothing about either: each relies on its record
    private bool OsHoldsKey(uint vk) => vk != WinVirtualKey.Return && IsKeyDown((int)vk);

    // Every event goes to the router, whose AdmitKey decides, except the deferred left Ctrl of a key the OS holds:
    // flushed during a visit by whatever follows it, the router would take it for the visit's own press.
    private void HandOver(KeyEvent? keyEvent)
    {
        if (keyEvent is null) return;
        if (keyEvent is { Type: KeyEventType.KeyDown, PhysicalKey: WinVirtualKey.LControl } && IsOnVirtualScreen && IsKeyDown(WinVirtualKey.LControl))
            return;
        _onKeyEvent?.Invoke(keyEvent);
    }

    internal enum Straddle { None, Echo, Rebase }

    [Flags]
    internal enum Hooks { None = 0, Mouse = 1, Keyboard = 2 }
}

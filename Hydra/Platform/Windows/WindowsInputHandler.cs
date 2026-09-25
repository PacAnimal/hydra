using System.Runtime.InteropServices;
using Cathedral.Extensions;
using Cathedral.Utils;
using Hydra.Config;
using Hydra.Keyboard;
using Hydra.Mouse;
using Microsoft.Extensions.Logging;

namespace Hydra.Platform.Windows;

public sealed class WindowsInputHandler(ILogger<WindowsInputHandler> log, IHydraProfile profile) : IPlatformInput
{
    // stored as fields to prevent GC collection while hooks are active
    private HookProc? _mouseHookProc;
    private HookProc? _keyboardHookProc;
    private nint _mouseHook;
    private nint _keyboardHook;
    private Thread? _hookThread;
    private uint _hookThreadId;
    private readonly WinKeyResolver _keyResolver = new();
    private Action<double, double>? _onMouseMove;
    private Action<double, double>? _onMouseDelta;
    // Written from both threads: the hook thread clears it after resyncing, the router thread (via
    // the IsOnVirtualScreen setter below) sets it on entry. volatile is the right tool for a flag
    // read on every hook callback and written from two places with no other ordering between them.
    private volatile bool _deltaReferenceIsStale = true;
    private Action<KeyEvent>? _onKeyEvent;
    private Action<MouseButtonEvent>? _onMouseButton;
    private Action<MouseScrollEvent>? _onMouseScroll;
    private readonly WindowsShieldWindow _shield = new();
    private int _lastWarpX = -1;
    private int _lastWarpY = -1;
    // The fixed point WarpCursor last targeted — kept clear of the local screen edges is the whole
    // reason a virtual-screen visit re-centres at all. Every real sample re-warps back to exactly
    // this, on this thread, synchronously (see MouseHookCallback) rather than waiting for the
    // router to ask for it.
    private int _warpTargetX = -1;
    private int _warpTargetY = -1;
    private readonly Toggle _isOnVirtualScreen = new();

    public bool IsOnVirtualScreen
    {
        get => _isOnVirtualScreen;
        set
        {
            // Mark the delta reference stale THE MOMENT this actually flips false->true — not
            // inferred later by having the hook thread notice the flag differs from what it last
            // saw. That polling approach missed a bounce fast enough to flip false->true->... with
            // zero hook messages landing in between; TrySet's CompareExchange makes "did THIS call
            // actually change it" race-free regardless, which polling a snapshot on a different
            // thread cannot be.
            //
            // This setter itself is not always called from the router's consumer thread — a
            // disconnect path (OnPeersChanged/OnRelayDisconnected) resumes a RunFence continuation
            // on an arbitrary thread-pool thread, unsynchronized with whatever the consumer thread
            // does next — but that is a pre-existing race in the router's own disconnect handling,
            // not something this flag introduces or could fix from here.
            if (_isOnVirtualScreen.TrySet(value) && value)
                _deltaReferenceIsStale = true;
        }
    }
    private nint _currentDesktop;
    private Timer? _healthTimer;

    // posted to the hook thread to trigger a desktop check
    private const uint WmCheckHealth = NativeMethods.WM_USER + 1;
    private const uint WmShieldShow = NativeMethods.WM_USER + 2;
    private const uint WmShieldHide = NativeMethods.WM_USER + 3;


    // low-level hooks work without elevation for non-elevated processes
    public bool IsAccessibilityTrusted() => true;

    public void WarpCursor(int x, int y)
    {
        _warpTargetX = x;
        _warpTargetY = y;
        _lastWarpX = x;
        _lastWarpY = y;
        NativeMethods.SetCursorPos(x, y);
    }

    public (int X, int Y)? GetCursorPosition()
    {
        if (!NativeMethods.GetCursorPos(out var p)) return null;
        return (p.x, p.y);
    }

    public ValueTask HideCursor()
    {
        // hide cursor immediately — fast counter op, safe inside hook callback
        _shield.HideCursorNow();
        // window management (SetWindowPos, SetForegroundWindow) is slow; post to hook thread
        // so it runs outside the hook callback and doesn't trigger the LL hook timeout
        NativeMethods.PostThreadMessage(_hookThreadId, WmShieldShow, nint.Zero, nint.Zero);
        return ValueTask.CompletedTask;
    }

    public ValueTask ShowCursor()
    {
        // restore cursor synchronously, same as HideCursor hides it — then post shield teardown
        _shield.ShowCursorNow();
        NativeMethods.PostThreadMessage(_hookThreadId, WmShieldHide, nint.Zero, nint.Zero);
        return ValueTask.CompletedTask;
    }

    public async Task StartEventTap(
        Action<double, double> onMouseMove,
        Action<double, double>? onMouseDelta,
        Action<KeyEvent> onKeyEvent,
        Action<MouseButtonEvent> onMouseButton,
        Action<MouseScrollEvent> onMouseScroll,
        Action? onLocalActivity = null)
    {
        _onMouseMove = onMouseMove;
        _onMouseDelta = onMouseDelta;
        _onKeyEvent = onKeyEvent;
        _onMouseButton = onMouseButton;
        _onMouseScroll = onMouseScroll;

        // callbacks stored as fields to prevent GC collection while hooks are active
        _mouseHookProc = MouseHookCallback;
        _keyboardHookProc = KeyboardHookCallback;

        var ready = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);

        _hookThread = new Thread(() =>
        {
            _hookThreadId = NativeMethods.GetCurrentThreadId();
            // pass null for hMod — LL hooks don't need a module handle (matches all reference KVM projects)
            _mouseHook = NativeMethods.SetWindowsHookExW(NativeMethods.WH_MOUSE_LL, _mouseHookProc, nint.Zero, 0);
            _keyboardHook = NativeMethods.SetWindowsHookExW(NativeMethods.WH_KEYBOARD_LL, _keyboardHookProc, nint.Zero, 0);

            if (_mouseHook == nint.Zero || _keyboardHook == nint.Zero)
            {
                log.LogError("SetWindowsHookEx failed -- could not install input hooks");
                ready.TrySetResult(false);
                return;
            }

            _currentDesktop = NativeMethods.GetThreadDesktop(_hookThreadId);
            WarnIfNotOnInputDesktop();
            _shield.Create(profile.DebugShield);
            ready.TrySetResult(true);

            // message pump — hooks fire during GetMessage
            while (NativeMethods.GetMessage(out var msg, nint.Zero, 0, 0) > 0)
            {
                if (msg.message == WmCheckHealth)
                {
                    CheckHookHealth();
                    continue;
                }
                if (msg.message == WmShieldShow)
                {
                    _shield.Show();
                    continue;
                }
                if (msg.message == WmShieldHide)
                {
                    _shield.Hide();
                    continue;
                }
                NativeMethods.TranslateMessage(in msg);
                NativeMethods.DispatchMessage(in msg);
            }

            _shield.Destroy();
            if (_mouseHook != nint.Zero) NativeMethods.UnhookWindowsHookEx(_mouseHook);
            if (_keyboardHook != nint.Zero) NativeMethods.UnhookWindowsHookEx(_keyboardHook);
        })
        { IsBackground = true, Name = "HydraHookPump" };

        _hookThread.Start();
        await ready.Task;

        // periodic hook health check — detects desktop changes (UAC, lock screen) that silently invalidate hooks
        _healthTimer = new Timer(_ =>
            NativeMethods.PostThreadMessage(_hookThreadId, WmCheckHealth, nint.Zero, nint.Zero),
            null, 200, 200);
    }

    public void StopEventTap()
    {
        _healthTimer?.Dispose();
        _healthTimer = null;
        if (_hookThreadId != 0)
            NativeMethods.PostThreadMessage(_hookThreadId, NativeMethods.WM_QUIT, nint.Zero, nint.Zero);
        _hookThread?.Join(TimeSpan.FromSeconds(2));
    }

    public bool AnyMouseButtonHeld()
    {
        // VK_LBUTTON=0x01, VK_RBUTTON=0x02, VK_MBUTTON=0x04, VK_XBUTTON1=0x05, VK_XBUTTON2=0x06
        // high bit (0x8000) set means the key is currently down
        return (NativeMethods.GetKeyState(0x01) & 0x8000) != 0
            || (NativeMethods.GetKeyState(0x02) & 0x8000) != 0
            || (NativeMethods.GetKeyState(0x04) & 0x8000) != 0
            || (NativeMethods.GetKeyState(0x05) & 0x8000) != 0
            || (NativeMethods.GetKeyState(0x06) & 0x8000) != 0;
    }

    public ValueTask DisposeAsync() { StopEventTap(); return ValueTask.CompletedTask; }

    // SetWindowsHookEx is desktop-scoped, and it reports success whichever desktop we are on. Started
    // outside the interactive session -- Task Scheduler, a service without CreateProcessAsUser, a
    // sandboxed launcher -- Hydra therefore logs screens, relay and peers exactly as usual and simply
    // receives no input, because the cursor is moving on a desktop our hooks do not cover.
    private void WarnIfNotOnInputDesktop()
    {
        var ours = WindowsDesktop.Name(_currentDesktop);
        var input = WindowsDesktop.InputDesktopName();
        if (ours.Length == 0 || input.Length == 0 || ours.EqualsIgnoreCase(input)) return;

        log.LogWarning(
            "Input hooks are installed on desktop '{Ours}' but input is going to '{Input}' -- no keyboard or mouse " +
            "events will arrive. Run Hydra as a Windows service (--service), which launches the session child on the " +
            "interactive desktop, or start it from an interactive logon session.",
            ours, input);
    }

    // called on the hook thread — checks if the desktop has changed and reinstalls hooks if needed
    private void CheckHookHealth()
    {
        var desk = NativeMethods.GetThreadDesktop(_hookThreadId);
        if (desk == _currentDesktop) return;
        _currentDesktop = desk;

        log.LogInformation("Desktop change detected, reinstalling hooks");
        if (_mouseHook != nint.Zero) { NativeMethods.UnhookWindowsHookEx(_mouseHook); _mouseHook = nint.Zero; }
        if (_keyboardHook != nint.Zero) { NativeMethods.UnhookWindowsHookEx(_keyboardHook); _keyboardHook = nint.Zero; }
        _mouseHook = NativeMethods.SetWindowsHookExW(NativeMethods.WH_MOUSE_LL, _mouseHookProc!, nint.Zero, 0);
        _keyboardHook = NativeMethods.SetWindowsHookExW(NativeMethods.WH_KEYBOARD_LL, _keyboardHookProc!, nint.Zero, 0);
        if (_mouseHook == nint.Zero || _keyboardHook == nint.Zero)
            log.LogWarning("Hook reinstall failed after desktop change");

        // emit key-up events for any held keys so the slave doesn't get stuck
        foreach (var keyUp in _keyResolver.TakeHeldKeyUps())
            _onKeyEvent?.Invoke(keyUp);

        // clear stale key state — modifier bits from the old desktop bleed into new events otherwise
        _keyResolver.Reset();

        // recreate shield on new desktop; re-show if we were on a virtual screen
        _shield.Destroy();
        _shield.Create(profile.DebugShield);
        if (IsOnVirtualScreen)
            _shield.Show();
    }

    private nint MouseHookCallback(int nCode, nint wParam, nint lParam)
    {
        if (nCode >= 0)
        {
            var info = Marshal.PtrToStructure<MSLLHOOKSTRUCT>(lParam);
            var msg = (int)wParam;

            if (msg == NativeMethods.WM_MOUSEMOVE)
            {
                // The router flips IsOnVirtualScreen true BEFORE the warp that is meant to prime the
                // delta reference actually runs (ApplyEnterScreen/AnchorAtWarpPoint sit in between,
                // on the OTHER thread) — a real sample landing in that gap would otherwise measure
                // against wherever the cursor was before the transition, an arbitrary and often huge
                // delta that lands the remote cursor in a random corner on entry. The IsOnVirtualScreen
                // setter above marks _deltaReferenceIsStale the instant that flip actually happens, on
                // the router's own thread — not inferred here by comparing against what this thread
                // last happened to see, which a fast enough bounce (enter, immediately kicked back
                // out, re-enter) could complete without this thread ever observing the "local" step in
                // between.
                var onVirtualScreen = IsOnVirtualScreen;

                // ignore synthetic events generated by our own WarpCursor call
                if (info.pt.x == _lastWarpX && info.pt.y == _lastWarpY)
                    return onVirtualScreen ? 1 : NativeMethods.CallNextHookEx(nint.Zero, nCode, wParam, lParam);

                if (onVirtualScreen)
                {
                    // Relative delta from consecutive readings on this one thread, immune to the
                    // "keep only the latest sample" coalescing an absolute position feeds into
                    // upstream — that silently drops whatever the cursor did between the sample
                    // being handled and a warp actually landing. _lastWarpX/Y double as the delta
                    // reference: WarpCursor sets them to the warp target, so the first real sample
                    // after a warp measures from there, not from wherever the cursor was before it.
                    // _deltaReferenceIsStale skips exactly the one sample that would otherwise
                    // measure across a transition: resync without reporting movement, rather than
                    // report garbage.
                    if (_deltaReferenceIsStale)
                    {
                        _lastWarpX = info.pt.x;
                        _lastWarpY = info.pt.y;
                        _deltaReferenceIsStale = false;
                    }
                    else
                    {
                        var dx = info.pt.x - _lastWarpX;
                        var dy = info.pt.y - _lastWarpY;
                        _onMouseDelta?.Invoke(dx, dy);

                        // Re-centre on THIS raw sample, synchronously, right here — not once per
                        // batch InputRouter gets around to processing (up to ~8ms and a whole
                        // batch's worth of real movement later). Windows' own cursor position is
                        // still the real, monitor-clamped one underneath this delta; nothing has
                        // changed that. What changed is HOW BIG the periodic reset jump looks to
                        // Windows' own ballistics/acceleration state: warping back every raw sample
                        // (~900/s) undoes at most one sample's worth of real movement each time —
                        // small, indistinguishable from ordinary jitter, exactly what commit
                        // 8964546 did and what never had this problem. Warping once per processed
                        // batch instead (what recentring every PROCESSED sample amounts to) undoes
                        // up to a whole batch's worth in one jump, and Windows can't tell that
                        // artificial reset apart from real input — its own documented behaviour is
                        // that continual SetCursorPos resets "can cause mouse movement recording to
                        // malfunction", and a batch-sized jump repeating every 8ms is precisely the
                        // pattern that provokes it. Keeping the reset sample-sized keeps it invisible
                        // to that state machine, same as it always was. The relay send/actor-post
                        // this delta feeds (PostMouseInput, upstream) is unaffected and stays
                        // batched to MaxMouseHz — only the warp itself moved back to per-sample,
                        // and a bare SetCursorPos costs nothing like the channel post that the
                        // batching in d2742e3 was actually paying for.
                        NativeMethods.SetCursorPos(_warpTargetX, _warpTargetY);
                        _lastWarpX = _warpTargetX;
                        _lastWarpY = _warpTargetY;
                    }
                }
                else
                {
                    _onMouseMove?.Invoke(info.pt.x, info.pt.y);
                }
            }
            else if (msg is NativeMethods.WM_LBUTTONDOWN or NativeMethods.WM_LBUTTONUP
                or NativeMethods.WM_RBUTTONDOWN or NativeMethods.WM_RBUTTONUP
                or NativeMethods.WM_MBUTTONDOWN or NativeMethods.WM_MBUTTONUP
                or NativeMethods.WM_XBUTTONDOWN or NativeMethods.WM_XBUTTONUP)
            {
                var isDown = msg is NativeMethods.WM_LBUTTONDOWN or NativeMethods.WM_RBUTTONDOWN
                    or NativeMethods.WM_MBUTTONDOWN or NativeMethods.WM_XBUTTONDOWN;
                var button = msg is NativeMethods.WM_XBUTTONDOWN or NativeMethods.WM_XBUTTONUP
                    ? (((info.mouseData >> 16) & 0xFFFF) == NativeMethods.XBUTTON1 ? MouseButton.Extra1 : MouseButton.Extra2)
                    : msg is NativeMethods.WM_RBUTTONDOWN or NativeMethods.WM_RBUTTONUP ? MouseButton.Right
                    : msg is NativeMethods.WM_MBUTTONDOWN or NativeMethods.WM_MBUTTONUP ? MouseButton.Middle
                    : MouseButton.Left;
                _onMouseButton?.Invoke(new MouseButtonEvent(button, isDown));
            }
            else if (msg is NativeMethods.WM_MOUSEWHEEL or NativeMethods.WM_MOUSEHWHEEL)
            {
                var delta = (short)(info.mouseData >> 16);
                var scroll = msg == NativeMethods.WM_MOUSEWHEEL
                    ? new MouseScrollEvent(0, delta)
                    : new MouseScrollEvent(delta, 0);
                _onMouseScroll?.Invoke(scroll);
            }
        }

        // swallow all mouse events while on virtual screen — cursor stays frozen at center
        if (IsOnVirtualScreen) return 1;
        return NativeMethods.CallNextHookEx(nint.Zero, nCode, wParam, lParam);
    }

    private nint KeyboardHookCallback(int nCode, nint wParam, nint lParam)
    {
        if (nCode >= 0)
        {
            var info = Marshal.PtrToStructure<KBDLLHOOKSTRUCT>(lParam);
            // always resolve to track modifier state even on the real screen
            var keyEvents = _keyResolver.Resolve((int)wParam, info);
            if (keyEvents is not null)
                foreach (var keyEvent in keyEvents)
                    _onKeyEvent?.Invoke(keyEvent);
            if (IsOnVirtualScreen) return 1; // swallow — don't call CallNextHookEx
        }
        return NativeMethods.CallNextHookEx(nint.Zero, nCode, wParam, lParam);
    }
}

using Hydra.Keyboard;
using Hydra.Relay;
using Hydra.Screen;
using Microsoft.Extensions.Logging;

namespace Hydra.Platform.Linux.Wayland;

// when the slave may start its injection session again. A failed start (the user cancelled the permission dialog,
// a portal error) waits 5 s, doubling to 60 s, before the next input may start another, so a dialog is not reopened on
// every burst of input. Each attempt carries the epoch it began in; a session loss starts a new one, so a device
// obtained across a loss is the dead session's and is discarded. Not thread-safe: the handler calls it under its lock.
internal sealed class SenderAcquisition(TimeProvider time)
{
    internal static readonly TimeSpan FirstRetry = TimeSpan.FromSeconds(5);
    internal static readonly TimeSpan MaxRetry = TimeSpan.FromSeconds(60);

    private int _epoch;
    private bool _inFlight;
    private TimeSpan? _delay;
    private DateTimeOffset? _retryAt;

    // the epoch of a new attempt; null while one is running or the backoff has not run out
    internal int? Begin()
    {
        if (_inFlight || (_retryAt is { } at && time.GetUtcNow() < at)) return null;
        _inFlight = true;
        return _epoch;
    }

    // the wait before the next attempt; null for an attempt a session loss outdated
    internal SenderRetry? Failed(int epoch)
    {
        if (epoch != _epoch) return null;
        _inFlight = false;
        var first = _delay == null;
        var delay = _delay is { } previous ? TimeSpan.FromTicks(Math.Min(previous.Ticks * 2, MaxRetry.Ticks)) : FirstRetry;
        _delay = delay;
        _retryAt = time.GetUtcNow() + delay;
        return new SenderRetry(delay, first);
    }

    // whether the device may be kept: false when a session loss came between the start and its answer
    internal bool Succeeded(int epoch)
    {
        if (epoch != _epoch) return false;
        _inFlight = false;
        _delay = null;
        _retryAt = null;
        return true;
    }

    internal void Lost()
    {
        _epoch++;
        _inFlight = false;
    }
}

internal sealed record SenderRetry(TimeSpan Delay, bool FirstOfStreak);

// the Wayland slave's injection, through the session's IInjectionDevice: as Deskflow's, an ei sender on the
// RemoteDesktop portal's EIS connection typing on the compositor's own keymap, or where the portal has no
// RemoteDesktop, the wlroots virtual pointer and keyboard typing on the keymap we gave them. Neither has unicode
// injection: a character the keymap lacks is composed with its dead keys where it can be, else typed on a key the
// wlroots device binds to it on a keymap of its own, else dropped. Emulation starts with the first event of a burst and stops once the
// visit ends or input has been idle a while, so the compositor can blank the screen.
internal sealed class WaylandOutputHandler : IPlatformOutput, ICursor
{
    private static readonly TimeSpan IdleStop = TimeSpan.FromSeconds(4);

    private readonly ILogger<WaylandOutputHandler> _log;
    private readonly Lock _lock = new();
    private readonly CancellationTokenSource _cancel = new();
    // a held key is the key the master pressed, whatever the keymap types it with
    private readonly Dictionary<HeldKey, uint> _heldKeys = [];
    private readonly HashSet<uint> _heldButtons = [];
    private readonly HashSet<char> _unmappable = [];
    private readonly HashSet<char> _otherGroup = [];
    // the first refusal of a character on another layout since the device came is logged where the user sees it
    private bool _loggedOtherGroup;
    // the group last selected on the device; null while it types under its own
    private uint? _selectedGroup;
    private readonly ShiftedKeypad _shiftedKeypad = new();
    private readonly NumLockRestore _numLockRestore = new();
    private IWaylandSession? _session;
    private IInjectionDevice? _device;
    private XkbKeymapLookup? _keymap;
    private string? _keymapText;
    private uint? _lockedMods;
    private bool _emulating;
    // whether this device has had its first absolute move since it was acquired or a master last left (EndVisit)
    private bool _nudged;
    private bool _loggedNotReady;
    private bool _disposed;
    private ITimer? _idleTimer;
    private ScreenPoint? _position;
    private readonly TimeProvider _time;
    private readonly SenderAcquisition _acquisition;

    public WaylandOutputHandler(IWaylandSession session, ILogger<WaylandOutputHandler> log)
        : this(session, log, TimeProvider.System)
    {
    }

    internal WaylandOutputHandler(IWaylandSession session, ILogger<WaylandOutputHandler> log, TimeProvider time)
    {
        _session = session;
        _log = log;
        _time = time;
        _acquisition = new SenderAcquisition(time);
        session.SessionLost += OnSessionLost;
        lock (_lock) Acquire();
    }

    // whether the session's device is held, for tests
    internal bool HasDevice
    {
        get { lock (_lock) return _device != null; }
    }

    private readonly record struct HeldKey(char? Character, SpecialKey? Key)
    {
        public string Describe() => Character is { } c ? $"U+{(int)c:X4}" : Key?.ToString() ?? "none";
    }

    public void MoveMouse(int x, int y)
    {
        lock (_lock)
        {
            if (Ready() is not { } device) return;
            if (device.AbsoluteRegions is { } regions)
            {
                var target = WaylandInjection.AbsoluteTarget(regions, x, y);
                // a compositor such as Hyprland focuses a surface only on real motion, and an entry often lands exactly
                // where its cursor already is, so the device's first move goes through a neighbouring pixel
                if (!_nudged && Neighbour(regions, target) is { } nudge) Emit(device, d => d.MotionAbsolute(nudge.X, nudge.Y));
                _nudged = true;
                Emit(device, d => d.MotionAbsolute(target.X, target.Y));
                _position = new ScreenPoint((int)target.X, (int)target.Y);
                return;
            }
            if (_position is { } last) Emit(device, d => d.Motion(x - last.X, y - last.Y));
            _position = new ScreenPoint(x, y);
        }
    }

    public void MoveMouseRelative(int dx, int dy)
    {
        lock (_lock)
        {
            if (Ready() is not { } device) return;
            Emit(device, d => d.Motion(dx, dy));
            if (_position is not { } last) return;
            // where the compositor stops it is unknown; the regions bound it as they bound an absolute move
            var regions = device.AbsoluteRegions ?? [];
            var moved = WaylandInjection.AbsoluteTarget(regions, last.X + dx, last.Y + dy);
            _position = new ScreenPoint((int)moved.X, (int)moved.Y);
        }
    }

    public void WarpCursor(int x, int y) => MoveMouse(x, y);

    public ScreenPoint? GetCursorPosition()
    {
        lock (_lock) return _position;
    }

    // a Wayland client cannot hide the compositor's cursor; the compositor shows it where we move it
    public ValueTask HideCursor() => ValueTask.CompletedTask;

    public ValueTask ShowCursor() => ValueTask.CompletedTask;

    public void InjectMouseButton(MouseButtonMessage msg)
    {
        if (WaylandInjection.EvdevButton(msg.Button) is not { } button) return;
        lock (_lock)
        {
            if (Ready() is not { } device) return;
            if (msg.IsPressed ? !_heldButtons.Add(button) : !_heldButtons.Remove(button)) return;
            Emit(device, d => d.Button(button, msg.IsPressed));
        }
    }

    public void InjectMouseScroll(MouseScrollMessage msg)
    {
        var scroll = WaylandInjection.Discrete(msg);
        if (scroll is { X: 0, Y: 0 }) return;
        lock (_lock)
        {
            if (Ready() is not { } device) return;
            Emit(device, d => d.Scroll(scroll.X, scroll.Y));
        }
    }

    public void InjectKey(KeyEventMessage msg)
    {
        lock (_lock)
        {
            if (Ready() is not { } device || Keymap(device) is not { } keymap) return;
            var isDown = msg.Type == KeyEventType.KeyDown;
            if (msg.Key is { } keypadKey) msg = msg with { Key = _shiftedKeypad.Injected(keypadKey, msg.Type, msg.Modifiers) };
            var held = new HeldKey(msg.Character, msg.Key);

            if (!isDown)
            {
                if (_heldKeys.Remove(held, out var code))
                    foreach (var stroke in WaylandInjection.Release(keymap, msg, code)) SendKey(device, stroke.EvdevKey, stroke.Pressed);
                return;
            }

            if (msg.Key == SpecialKey.MissionControl)
            {
                // tap Super_L to open GNOME Activities / KDE Overview
                if (WaylandInjection.KeysymCode(keymap, XorgVirtualKey.Super_L) is not { } super) return;
                SendKey(device, super, true);
                SendKey(device, super, false);
                return;
            }

            if (msg.Key == SpecialKey.NumLock) _numLockRestore.Toggled();
            var syncsLocks = msg.Key is not (SpecialKey.CapsLock or SpecialKey.NumLock);
            if (syncsLocks) SyncLock(device, keymap, XorgVirtualKey.CapsLock, keymap.CapsLockMask, (msg.Modifiers & KeyModifiers.CapsLock) != 0);

            // a repeat lets go of the held key and presses it again, as Deskflow's does: the client's own repeat
            // timer restarts each time, so it never repeats on top of the master's
            if (_heldKeys.Remove(held, out var repeated)) SendKey(device, repeated, false);

            var group = device.Group;
            // a device that selects groups types in the one selected last, so text on another layout switches once
            var effective = _selectedGroup ?? group;
            var plan = WaylandInjection.Plan(keymap, msg, HeldModifiers(), ((_lockedMods ?? 0) & keymap.CapsLockMask) != 0, effective, device.SelectsGroup);
            // a character only another layout group types: under the compositor's group its key types something else.
            // a shortcut is never refused, its key going in under the compositor's group as apps match it
            if (plan == null && !device.SelectsGroup && msg.Character is { } elsewhere && !WaylandInjection.IsShortcut(msg.Modifiers)
                && WaylandInjection.OnOtherGroup(keymap, elsewhere, group))
            {
                LogOtherGroup(elsewhere, device);
                return;
            }
            // a character the keymap neither types nor composes, on a key bound to it where the device can upload a keymap
            if (plan == null && msg.Character is { } unmapped && device.BindKeysym(XkbKeymapLookup.CharToKeysym(unmapped)) is { } bound)
                plan = WaylandInjection.PlanBound(bound);
            // a shortcut neither the keymap nor a bound key types, on a keymap with no Latin group: the us reference's key
            if (plan == null && msg.Character is { } shortcut && WaylandInjection.IsShortcut(msg.Modifiers) && keymap.LatinGroup == null)
                plan = WaylandInjection.PlanReference(keymap, shortcut, HeldModifiers(), ((_lockedMods ?? 0) & keymap.CapsLockMask) != 0);
            if (plan == null)
            {
                if (msg.Character is { } ch && _unmappable.Add(ch) && _log.IsEnabled(LogLevel.Debug))
                    _log.LogDebug("Character U+{Code:X4} is not on the keymap and its dead keys cannot compose it; {Device} cannot type it", (int)ch, device.Name);
                return;
            }
            // NumLock only before a key it changes, never for the master's bit on plain typing
            if (syncsLocks && plan.NumLock is { } numLock && SyncLock(device, keymap, XorgVirtualKey.NumLock, keymap.NumLockMask, numLock))
                _numLockRestore.Synced(had: !numLock);
            // a lock key leaves the lock unknown until the device reports it again: EIS once the compositor answers,
            // a device that keeps its own modifiers as the key goes in
            if (msg.Key is SpecialKey.CapsLock or SpecialKey.NumLock) _lockedMods = null;
            // another layout group's key goes in under that group where the device can select it, kept selected until a
            // key needs another, so a word on that layout switches once; the device's own group selects null, and a key
            // every group types alike (a special key, a bound one) leaves the selection as it is
            if (plan.Group is { } planned) SelectGroup(device, planned == group ? null : planned);
            foreach (var stroke in plan.Strokes) SendKey(device, stroke.EvdevKey, stroke.Pressed);
            _heldKeys[held] = plan.Key;
        }
    }

    // under _lock: the first refusal of a visit's device is the user's to see, the rest only in the debug log
    private void LogOtherGroup(char ch, IInjectionDevice device)
    {
        if (!_otherGroup.Add(ch)) return;
        if (!_loggedOtherGroup)
        {
            _loggedOtherGroup = true;
            if (_log.IsEnabled(LogLevel.Information))
                _log.LogInformation("{Char} is on a layout the compositor has not selected; switch the slave's layout to type it", ch);
        }
        else if (_log.IsEnabled(LogLevel.Debug))
        {
            _log.LogDebug("{Char} is on a layout the compositor has not selected; {Device} cannot type it", ch, device.Name);
        }
    }

    // under _lock; only a change is sent
    private void SelectGroup(IInjectionDevice device, uint? group)
    {
        if (group == _selectedGroup || !device.SelectsGroup) return;
        _selectedGroup = group;
        Emit(device, d => d.SelectGroup(group));
    }

    public void EndVisit()
    {
        lock (_lock)
        {
            if (_device is { } device)
            {
                ReleaseAll(device);
                if (_numLockRestore.Take() is { } before && Keymap(device) is { } keymap)
                    SyncLock(device, keymap, XorgVirtualKey.NumLock, keymap.NumLockMask, before);
                StopEmulating(device);
            }
            // the next entry may land where the cursor rests again
            _nudged = false;
        }
    }

    public void Dispose()
    {
        lock (_lock)
        {
            if (_disposed) return;
            _disposed = true;
            if (_device is { } device)
            {
                ReleaseAll(device);
                StopEmulating(device);
            }
            Detach();
            if (_session is { } session) session.SessionLost -= OnSessionLost;
            _session = null;
            _idleTimer?.Dispose();
            _idleTimer = null;
        }
        _cancel.Cancel();
        _cancel.Dispose();
    }

    // a pixel next to the target inside its region: right, else left, else above or below for a region one pixel wide;
    // null for a one-pixel region
    private static EiPoint? Neighbour(IReadOnlyList<ScreenBounds> regions, EiPoint target)
    {
        var region = regions.FirstOrDefault(r => target.X >= r.X && target.X < r.X + r.Width && target.Y >= r.Y && target.Y < r.Y + r.Height);
        if (region == null) return regions.Count == 0 ? target with { X = target.X + 1 } : null;
        if (target.X + 1 < region.X + region.Width) return target with { X = target.X + 1 };
        if (target.X - 1 >= region.X) return target with { X = target.X - 1 };
        if (target.Y + 1 < region.Y + region.Height) return target with { Y = target.Y + 1 };
        if (target.Y - 1 >= region.Y) return target with { Y = target.Y - 1 };
        return null;
    }

    // -- session --

    // under _lock
    private void Acquire()
    {
        if (_disposed || _device != null || _session is not { } session || _acquisition.Begin() is not { } epoch) return;
        var cancel = _cancel.Token;
        Acquiring = Task.Run(() => AcquireAsync(session, epoch, cancel));
    }

    // the latest attempt to get the session's device, for tests to wait on
    internal Task Acquiring { get; private set; } = Task.CompletedTask;

    private async Task AcquireAsync(IWaylandSession session, int epoch, CancellationToken cancel)
    {
        IInjectionDevice? device = null;
        Exception? failure = null;
        try
        {
            device = await session.Injection(cancel).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancel.IsCancellationRequested)
        {
            return;
        }
        catch (Exception e)
        {
            failure = e;
        }
        lock (_lock)
        {
            if (_disposed) return;
            if (device == null)
            {
                if (_acquisition.Failed(epoch) is not { } retry) return;
                if (retry.FirstOfStreak && _log.IsEnabled(LogLevel.Warning))
                    _log.LogWarning(failure, "Cannot start input injection (RemoteDesktop or wlroots virtual input); retrying in {Delay}", retry.Delay);
                else if (_log.IsEnabled(LogLevel.Debug))
                    _log.LogDebug("Input injection still not started; retrying in {Delay}: {Error}", retry.Delay, failure?.Message);
                return;
            }
            // a device from before a session loss is the dead session's
            if (!_acquisition.Succeeded(epoch)) return;
            _device = device;
            _nudged = false;
            device.LockedModifiersChanged += OnLockedModifiers;
            device.Lost += OnDeviceLost;
            _lockedMods = device.LockedModifiers;
            _loggedNotReady = false;
            if (_log.IsEnabled(LogLevel.Information)) _log.LogInformation("Injecting input through {Device}", device.Name);
            // a loss before the handlers were attached
            if (device.IsLost) Detach();
        }
    }

    private void OnSessionLost()
    {
        lock (_lock)
        {
            if (_disposed) return;
            _acquisition.Lost();
            Detach();
            _heldKeys.Clear();
            _heldButtons.Clear();
        }
    }

    // the session raises SessionLost for it too; this only stops sending into a dead connection sooner
    private void OnDeviceLost()
    {
        lock (_lock)
        {
            if (_disposed) return;
            _acquisition.Lost();
            Detach();
            _heldKeys.Clear();
            _heldButtons.Clear();
        }
    }

    // under _lock; the session owns the device, so it is let go of, never disposed
    private void Detach()
    {
        if (_device is { } device)
        {
            device.LockedModifiersChanged -= OnLockedModifiers;
            device.Lost -= OnDeviceLost;
        }
        _device = null;
        _nudged = false;
        _emulating = false;
        _lockedMods = null;
        _selectedGroup = null;
        _loggedOtherGroup = false;
        _otherGroup.Clear();
        _keymap?.Dispose();
        _keymap = null;
        _keymapText = null;
    }

    private void OnLockedModifiers(uint locked)
    {
        lock (_lock) _lockedMods = locked;
    }

    // under _lock; null, and acquiring, until the session's device is there
    private IInjectionDevice? Ready()
    {
        if (_disposed) return null;
        if (_device is { } device) return device;
        Acquire();
        if (!_loggedNotReady && _log.IsEnabled(LogLevel.Information))
            _log.LogInformation("Input injection not ready yet; dropping input until it is");
        _loggedNotReady = true;
        return null;
    }

    // under _lock; rebuilt whenever the device's keymap changes
    private XkbKeymapLookup? Keymap(IInjectionDevice device)
    {
        var text = device.Keymap;
        if (ReferenceEquals(text, _keymapText)) return _keymap;
        _keymap?.Dispose();
        _keymap = null;
        _keymapText = text;
        if (text == null)
        {
            if (_log.IsEnabled(LogLevel.Warning)) _log.LogWarning("{Device} offers no keyboard with an XKB keymap; keys are dropped", device.Name);
            return null;
        }
        try
        {
            _keymap = new XkbKeymapLookup(text);
        }
        catch (InvalidOperationException e)
        {
            if (_log.IsEnabled(LogLevel.Warning)) _log.LogWarning(e, "Cannot compile the keymap of {Device}; keys are dropped", device.Name);
        }
        return _keymap;
    }

    // -- injection, all under _lock --

    private HeldModifierKeys HeldModifiers()
    {
        List<uint>? shift = null, altGr = null;
        foreach (var (key, code) in _heldKeys)
        {
            if (key.Key is SpecialKey.Shift_L or SpecialKey.Shift_R) (shift ??= []).Add(code);
            else if (key.Key == SpecialKey.AltGr) (altGr ??= []).Add(code);
        }
        return shift == null && altGr == null ? HeldModifierKeys.None : new HeldModifierKeys(shift ?? [], altGr ?? []);
    }

    // returns whether it flipped the lock; an unknown lock state is left alone
    private bool SyncLock(IInjectionDevice device, XkbKeymapLookup keymap, ulong keysym, uint mask, bool want)
    {
        // a keymap without the lock's modifier cannot report it, so it is left alone
        if (mask == 0 || _lockedMods is not { } locked || ((locked & mask) != 0) == want) return false;
        if (WaylandInjection.KeysymCode(keymap, keysym) is not { } code) return false;
        SendKey(device, code, true);
        SendKey(device, code, false);
        // the compositor's modifiers event confirms it later; until then this is what the tap left
        _lockedMods = want ? locked | mask : locked & ~mask;
        return true;
    }

    private void SendKey(IInjectionDevice device, uint code, bool pressed) => Emit(device, d => d.Key(code, pressed));

    private void Emit(IInjectionDevice device, Func<IInjectionDevice, bool> send)
    {
        if (!_emulating)
        {
            device.StartEmulating();
            _emulating = true;
        }
        if (send(device)) device.Frame();
        ArmIdleStop();
    }

    private void ReleaseAll(IInjectionDevice device)
    {
        if (_heldKeys.Count > 0 && _log.IsEnabled(LogLevel.Debug))
            _log.LogDebug("Letting go of the held keys: {Keys}", string.Join(", ", _heldKeys.Keys.Select(k => k.Describe())));
        foreach (var code in _heldKeys.Values) SendKey(device, code, false);
        _heldKeys.Clear();
        // the device's own group back, so the user's layout is as they left it
        SelectGroup(device, null);
        foreach (var button in _heldButtons) Emit(device, d => d.Button(button, false));
        _heldButtons.Clear();
    }

    private void StopEmulating(IInjectionDevice device)
    {
        _idleTimer?.Change(Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan);
        if (!_emulating) return;
        device.StopEmulating();
        _emulating = false;
    }

    private void ArmIdleStop()
    {
        _idleTimer ??= _time.CreateTimer(_ => OnIdle(), null, Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan);
        _idleTimer.Change(IdleStop, Timeout.InfiniteTimeSpan);
    }

    // a key or button still held keeps emulation on, or the compositor would see it held without a device
    private void OnIdle()
    {
        lock (_lock)
        {
            if (_disposed || _device is not { } device) return;
            if (_heldKeys.Count > 0 || _heldButtons.Count > 0)
            {
                _idleTimer?.Change(IdleStop, Timeout.InfiniteTimeSpan);
                return;
            }
            StopEmulating(device);
        }
    }
}

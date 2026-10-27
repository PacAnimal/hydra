using System.Runtime.InteropServices;
using Hydra.Screen;
using Microsoft.Extensions.Logging;

namespace Hydra.Platform.Linux;

// X11's IWindowFocus, on a display connection of its own and off the caller's thread, since it asks the server about
// every window. The stacking order comes from the window manager (_NET_CLIENT_LIST_STACKING), else from the root's
// children; only ordinary windows count, by their EWMH type and state. A window manager is asked to activate the window
// (_NET_ACTIVE_WINDOW), and without one it is raised and given the input focus directly.
public sealed class XorgWindowFocus : IWindowFocus, IDisposable
{
    private readonly ILogger<XorgWindowFocus> _log;
    private readonly Lock _lock = new();
    private readonly nint _display;
    private readonly nint _root;
    private readonly nint _clientListStacking;
    private readonly nint _activeWindow;
    private readonly nint _windowType;
    private readonly nint _wmState;
    private readonly nint _stateHidden;
    // the types a window a user works in has; one with no type is normal
    private readonly HashSet<nint> _ordinaryTypes;

    public XorgWindowFocus(ILogger<XorgWindowFocus> log)
    {
        _log = log;
        _display = XlibRuntime.OpenDisplayOrThrow("window focus");
        _root = NativeMethods.XDefaultRootWindow(_display);
        _clientListStacking = NativeMethods.XInternAtom(_display, "_NET_CLIENT_LIST_STACKING", false);
        _activeWindow = NativeMethods.XInternAtom(_display, "_NET_ACTIVE_WINDOW", false);
        _windowType = NativeMethods.XInternAtom(_display, "_NET_WM_WINDOW_TYPE", false);
        _wmState = NativeMethods.XInternAtom(_display, "_NET_WM_STATE", false);
        _stateHidden = NativeMethods.XInternAtom(_display, "_NET_WM_STATE_HIDDEN", false);
        _ordinaryTypes = [NativeMethods.XInternAtom(_display, "_NET_WM_WINDOW_TYPE_NORMAL", false), NativeMethods.XInternAtom(_display, "_NET_WM_WINDOW_TYPE_DIALOG", false)];
    }

    public void FocusFrontmostOn(ScreenBounds screen) => _ = Task.Run(() => Focus(screen));

    internal void Focus(ScreenBounds screen)
    {
        lock (_lock)
        {
            var managed = ManagedStack();
            if (WindowPicker.FrontmostOn(Listed(managed ?? RootChildren()), screen) is not { } window)
            {
                if (_log.IsEnabled(LogLevel.Debug)) _log.LogDebug("Window focus: no window on {Screen}", screen);
                return;
            }
            if (managed != null) AskToActivate(window.Id);
            else
            {
                _ = NativeMethods.XRaiseWindow(_display, window.Id);
                _ = NativeMethods.XSetInputFocus(_display, window.Id, NativeMethods.RevertToParent, 0);
            }
            _ = NativeMethods.XFlush(_display);
        }
    }

    // the window manager's client windows, bottom to top; null without a window manager that keeps the list
    private nint[]? ManagedStack() => Longs(_root, _clientListStacking, NativeMethods.XA_WINDOW) is { Length: > 0 } windows ? windows : null;

    // a format 32 list property (windows or atoms); null when the window does not have it
    private nint[]? Longs(nint window, nint property, nint type)
    {
        var rc = NativeMethods.XGetWindowProperty(_display, window, property, 0, 4096, false, type,
            out _, out var format, out var count, out _, out var prop);
        try
        {
            if (rc != 0 || prop == 0 || format != 32) return null;
            // format 32 items are C longs, so pointer-sized
            var items = new nint[count];
            Marshal.Copy(prop, items, 0, (int)count);
            return items;
        }
        finally
        {
            if (prop != 0) _ = NativeMethods.XFree(prop);
        }
    }

    // not a dock, panel, desktop, menu or the like, and not minimised; a window manager lists those too
    private bool IsOrdinary(nint window)
    {
        if (Longs(window, _windowType, NativeMethods.XA_ATOM) is [var preferred, ..] && !_ordinaryTypes.Contains(preferred)) return false;
        return Longs(window, _wmState, NativeMethods.XA_ATOM) is not { } state || !state.Contains(_stateHidden);
    }

    // the root's children, bottom to top
    private nint[] RootChildren()
    {
        if (!NativeMethods.XQueryTree(_display, _root, out _, out _, out var children, out var count) || children == 0) return [];
        try
        {
            var windows = new nint[count];
            Marshal.Copy(children, windows, 0, (int)count);
            return windows;
        }
        finally
        {
            _ = NativeMethods.XFree(children);
        }
    }

    // front first, each mapped and ordinary, with its bounds in root coordinates
    private IEnumerable<ListedWindow> Listed(nint[] bottomToTop)
    {
        for (var i = bottomToTop.Length - 1; i >= 0; i--)
        {
            var window = bottomToTop[i];
            if (NativeMethods.XGetWindowAttributes(_display, window, out var attributes) == 0) continue;
            if (attributes.MapState != NativeMethods.IsViewable || attributes.OverrideRedirect != 0 || attributes.Class != NativeMethods.InputOutput) continue;
            if (!IsOrdinary(window)) continue;
            if (!NativeMethods.XTranslateCoordinates(_display, window, _root, 0, 0, out var x, out var y, out _)) continue;
            yield return new ListedWindow(window, 0, new ScreenBounds(x, y, attributes.Width, attributes.Height));
        }
    }

    // EWMH: a pager's request (source 2), which a window manager honours without focus-stealing prevention
    private void AskToActivate(nint window)
    {
        var ev = new XEvent
        {
            Type = NativeMethods.ClientMessage,
            SendEvent = 1,
            EventDisplay = _display,
            EventWindow = window,
            ClientMessageType = _activeWindow,
            ClientMessageFormat = 32,
            ClientMessageData0 = 2,
        };
        _ = NativeMethods.XSendEvent(_display, _root, false, NativeMethods.SubstructureRedirectMask | NativeMethods.SubstructureNotifyMask, ref ev);
    }

    public void Dispose()
    {
        lock (_lock)
            _ = NativeMethods.XCloseDisplay(_display);
    }
}

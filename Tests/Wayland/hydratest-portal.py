#!/usr/bin/env python3
"""Hydra integration-test xdg-desktop-portal backend.

Implements org.freedesktop.impl.portal.{InputCapture,RemoteDesktop,Clipboard,
GlobalShortcuts} for headless CI desktops: every request is approved at once (no dialogs) and
forwarded to the compositor's private API, picked by HYDRATEST_COMPOSITOR
("mutter" or "kwin"). On kwin the clipboard is a data-control client of our own, as xdg-desktop-portal-kde's: a
session's selection asks the session for each paste as it comes (SelectionTransfer), long after the call that set it,
its device's selection events say who owns the selection (a source of ours, else another client), and a read asks the
current offer through the same device. The real xdg-desktop-portal frontend in front of this backend lets an
InputCapture session set or read the selection only while its capture is on, and forwards SelectionTransfer whenever.
Zones come from HYDRATEST_ZONES ("WxH+X+Y,...") or else from GDK's monitors.
Global shortcuts fire when the tests' hands report a bound trigger's keys (org.hydratest.Control.Key), or by id
(Control.Trigger), via org.hydratest.Control at /org/hydratest/Control. Control.Selection reports the latest
selection owner change the compositor told the backend of (serial, whether a session owned it, its types).
Requires python3-gobject; no pip packages.
"""
import array
import os
import re
import socket
import struct
import sys
import traceback

from gi.repository import Gio, GLib

NAME = "org.freedesktop.impl.portal.desktop.hydratest"
OBJ = "/org/freedesktop/portal/desktop"
IMPL = "org.freedesktop.impl.portal."
CAPS = 3  # KEYBOARD | POINTER
PROPS = {
    "InputCapture": {"version": 2, "SupportedCapabilities": CAPS},
    "RemoteDesktop": {"version": 2, "AvailableDeviceTypes": CAPS},
    "Clipboard": {"version": 1},
    "GlobalShortcuts": {"version": 1},  # v2 adds ConfigureShortcuts (a dialog), not implemented
    "Session": {"version": 1},
}

MIC = "org.gnome.Mutter.InputCapture"
MRD = "org.gnome.Mutter.RemoteDesktop"
KWIN = "org.kde.KWin"
KIC = "org.kde.KWin.EIS.InputCapture"
KRD = "org.kde.KWin.EIS.RemoteDesktop"

COMPOSITOR = os.environ.get("HYDRATEST_COMPOSITOR")
MUTTER = COMPOSITOR == "mutter"
BUS = Gio.bus_get_sync(Gio.BusType.SESSION)


def iface_info(name):
    path = f"/usr/share/dbus-1/interfaces/{IMPL}{name}.xml"
    with open(path) as f:
        return Gio.DBusNodeInfo.new_for_xml(f.read()).lookup_interface(IMPL + name)


def u(value):
    return GLib.Variant("u", value)


def call(dest, path, iface, method, args=None, reply=None):
    reply = GLib.VariantType(reply) if reply else None
    res = BUS.call_sync(dest, path, iface, method, args, reply, Gio.DBusCallFlags.NONE, -1, None)
    return res.unpack() if reply else None


def call_fd(dest, path, iface, method, args=None, reply="(h)"):
    """Returns (reply variant, UnixFDList), ready to pass back to our caller."""
    return BUS.call_with_unix_fd_list_sync(
        dest, path, iface, method, args, GLib.VariantType(reply), Gio.DBusCallFlags.NONE, -1, None, None)


def fd_reply(fd):
    return GLib.Variant("(h)", (0,)), Gio.UnixFDList.new_from_array([fd])


def restore_data(value):
    return GLib.Variant("(suv)", ("hydratest", 1, u(value)))


def kwin_barrier(x1, y1, x2, y2, zones):
    """Port of xdg-desktop-portal-kde's checkAndMakeBarrier: None if rejected, else KWin's (x1, y1, x2, y2).

    Accepts only a barrier that fills one whole screen edge not shared with another screen,
    and moves a right/bottom-edge barrier onto the last pixel column/row (KWin hit-tests exactly).
    """
    if x1 != x2 and y1 != y2:
        return None  # Diagonal
    transpose = x1 != x2  # horizontal: work on the transposed geometry so the barrier is vertical
    if transpose:
        x1, y1, x2, y2 = y1, x1, y2, x2
    y1, y2 = min(y1, y2), max(y1, y2)
    found = on_right = False
    for w, h, x, y in zones:
        if transpose:
            w, h, x, y = h, w, y, x
        bottom = y + h - 1  # QRect::bottom()
        if y1 > bottom or y > y2:
            continue
        if x1 in (x, x + w):
            if y1 == y and y2 == bottom and not found:
                found, on_right = True, x1 == x + w
            else:
                return None  # BetweenScreensOrDoesNotFill
    if not found:
        return None  # NotOnEdge
    if on_right:
        x1, x2 = x1 - 1, x2 - 1
    return (y1, x1, y2, x2) if transpose else (x1, y1, x2, y2)


def kwin_zones():
    spec = os.environ.get("HYDRATEST_ZONES")
    if spec:
        pattern = r"(\d+)x(\d+)\+(-?\d+)\+(-?\d+)"
        return [tuple(int(n) for n in re.fullmatch(pattern, z.strip()).groups()) for z in spec.split(",")]
    monitors = GDK.get_monitors()
    geos = [monitors.get_item(i).get_geometry() for i in range(monitors.get_n_items())]
    return [(g.width, g.height, g.x, g.y) for g in geos]


class Session:
    def __init__(self, backend, handle, kind):
        self.handle, self.kind = handle, kind
        self.path = None  # compositor input-capture / remote-desktop object
        self.clip = None  # mutter remote-desktop session serving the clipboard
        self.clipboard = self.started = False
        self.devices, self.persist = CAPS, 0
        self.barriers = {}  # mutter: mutter barrier id -> portal id; kwin: portal id -> position
        self.mserial = 0  # mutter's zone serial from the last GetZones
        self.cookies = []  # kwin remote-desktop EIS connections
        self.shortcuts = {}  # global-shortcut id -> (description, trigger)
        self.subs = []
        self.reg = BUS.register_object(handle, SESSION, self.on_close, backend.get_property, None)
        backend.sessions[handle] = self

    def on_close(self, *args):
        BACKEND.close(self, closed_by_compositor=False)
        args[-1].return_value(None)


class DataControl:
    """The selection as xdg-desktop-portal-kde holds it: a Wayland data-control client (ext_data_control_v1, else
    wlr_data_control_unstable_v1, as wl-clipboard speaks them) whose source offers a session's types and asks for each
    paste as it comes (on_send(owner, mime, fd)), the fd to be written and closed once the session answers. Each
    selection the device reports is passed on (on_selection(owner, types)), its owner the session whose source it is,
    else None. Spoken over the raw wire protocol, since the image has no Wayland binding for python."""

    def __init__(self, on_send, on_selection):
        self.on_send = on_send
        self.on_selection = on_selection
        self.sock = socket.socket(socket.AF_UNIX, socket.SOCK_STREAM)
        display = os.environ.get("WAYLAND_DISPLAY", "wayland-0")
        self.sock.connect(display if display.startswith("/") else os.path.join(os.environ["XDG_RUNTIME_DIR"], display))
        self.next_id, self.buf, self.fds, self.waiting = 2, b"", [], None
        self.sources = {}  # data-control source id -> the session it serves
        # sources cancelled or destroyed whose id the compositor has not released (delete_id): a send still queued for
        # one carries an fd, which must be taken off the queue and closed
        self.dead_sources = set()
        self.current = None  # our source last set as the selection, until another client's cancels it
        self.offers = {}  # data-control offer id -> the types it offers
        self.selection = None  # the offer the device last reported as the selection
        # the sync sent after our latest set_selection, until done: the compositor has taken that source once it is,
        # and a selection reported before then may still be the one ours replaced
        self.settling = None
        self.manager = self.device = self.seat = None
        self.globals = {}  # interface -> (name, version)
        registry = self.new_id()
        self.send(1, 1, struct.pack("<I", registry))  # wl_display.get_registry
        self.registry = registry
        self.waiting = self.new_id()
        self.send(1, 0, struct.pack("<I", self.waiting))  # wl_display.sync: the globals are in once it is done
        while self.waiting is not None:
            if not self.read():
                raise RuntimeError("the compositor closed the connection")
        manager = next((i for i in ("ext_data_control_manager_v1", "zwlr_data_control_manager_v1") if i in self.globals), None)
        if manager is None or "wl_seat" not in self.globals:
            raise RuntimeError("the compositor offers no data-control manager and seat")
        self.seat = self.bind("wl_seat", 1)
        self.manager = self.bind(manager, 1)
        self.get_device()
        GLib.io_add_watch(self.sock.fileno(), GLib.PRIORITY_DEFAULT, GLib.IOCondition.IN | GLib.IOCondition.HUP,
                          self.on_readable)

    def get_device(self):
        self.device = self.new_id()
        self.send(self.manager, 1, struct.pack("<II", self.device, self.seat))  # get_data_device(new_id, seat)

    def on_readable(self, _fd, cond):
        """The watch stays as long as the connection does: an event that fails to handle is logged, never ends it."""
        try:
            if self.read():
                return True
        except Exception:
            traceback.print_exc()
            return True
        print("hydratest-portal: the data-control connection closed", file=sys.stderr)
        return False

    def new_id(self):
        self.next_id += 1
        return self.next_id - 1

    def bind(self, interface, version):
        name, _ = self.globals[interface]
        oid = self.new_id()
        self.send(self.registry, 0, struct.pack("<I", name) + wl_string(interface) + struct.pack("<II", version, oid))
        return oid

    def send(self, obj, opcode, payload, fd=None):
        message = struct.pack("<II", obj, (8 + len(payload)) << 16 | opcode) + payload
        if fd is None:
            self.sock.sendall(message)
            return
        # an fd argument takes no bytes in the message: it rides beside it, as SCM_RIGHTS
        sent = self.sock.sendmsg([message], [(socket.SOL_SOCKET, socket.SCM_RIGHTS, array.array("i", [fd]).tobytes())])
        if sent < len(message):
            self.sock.sendall(message[sent:])

    def receive(self, mime):
        """The current selection as mime, as xdg-desktop-portal-kde's SelectionRead has it: the read end of a pipe
        whose write end goes to the offer (receive(string, fd)); None when there is no selection or it lacks mime."""
        if self.selection is None or mime not in self.offers.get(self.selection, []):
            return None
        r, w = os.pipe2(os.O_CLOEXEC)
        try:
            self.send(self.selection, 0, wl_string(mime), fd=w)
        finally:
            os.close(w)
        return r

    def destroy_source(self, source):
        """A source is forgotten once cancelled, but its id stays known until the compositor releases it, so a send
        already on its way to it still has its fd taken off the queue."""
        self.sources.pop(source, None)
        if self.current == source:
            self.current = None
        self.dead_sources.add(source)
        self.send(source, 1, b"")  # destroy

    def set_selection(self, owner, mimes):
        if not mimes:
            self.current = None
            self.send(self.device, 0, struct.pack("<I", 0))
        else:
            source = self.new_id()
            self.send(self.manager, 0, struct.pack("<I", source))  # create_data_source(new_id)
            for mime in mimes:
                self.send(source, 0, wl_string(mime))  # offer(string)
            self.send(self.device, 0, struct.pack("<I", source))  # set_selection(object)
            self.sources[source] = owner
            self.current = source
        self.settling = self.new_id()
        self.send(1, 0, struct.pack("<I", self.settling))  # wl_display.sync(new_id)

    def closed(self, owner):
        """As the portal does for a session closed while it owns the selection: the selection is unset."""
        if self.current is not None and self.sources.get(self.current) == owner:
            self.current = None
            self.send(self.device, 0, struct.pack("<I", 0))

    # the selection the device reported last, as its owner and types: ours only as the outcome of our own latest set,
    # reported once its sync is done (another client's taking in between has cancelled our source by then)
    def report(self, ours):
        owner = self.sources.get(self.current) if ours and self.selection is not None and self.current is not None else None
        self.on_selection(owner, list(self.offers.get(self.selection, [])) if self.selection is not None else [])

    # Reads what the socket has and handles every whole event; False once it is gone. One recvmsg carries at most one
    # compositor sendmsg's fds (libwayland sends up to 28 at once); the room is ample, and a truncated batch is
    # reported, since every later send would be handed the wrong fd
    def read(self):
        data, anc, flags, _ = self.sock.recvmsg(65536, socket.CMSG_SPACE(64 * array.array("i").itemsize))
        if not data:
            return False
        if flags & socket.MSG_CTRUNC:
            print("hydratest-portal: data-control fds were truncated", file=sys.stderr)
        for level, kind, fds in anc:
            if level == socket.SOL_SOCKET and kind == socket.SCM_RIGHTS:
                fd_array = array.array("i")
                fd_array.frombytes(fds[:len(fds) - len(fds) % fd_array.itemsize])
                self.fds.extend(fd_array)
        self.buf += data
        while len(self.buf) >= 8:
            obj, word = struct.unpack_from("<II", self.buf)
            size, opcode = word >> 16, word & 0xFFFF
            if len(self.buf) < size:
                break
            body, self.buf = self.buf[8:size], self.buf[size:]
            # one event failing leaves the rest to handle, the stream still aligned
            try:
                self.event(obj, opcode, body)
            except Exception:
                traceback.print_exc()
        return True

    def event(self, obj, opcode, body):
        if obj == 1 and opcode == 0:  # wl_display.error(object, uint code, string message): the connection ends
            target, code = struct.unpack_from("<II", body)
            raise RuntimeError(f"wayland error {code} on object {target}: {read_string(body, 8)}")
        if obj == 1 and opcode == 1:  # wl_display.delete_id(uint): the compositor has released a client id
            self.dead_sources.discard(struct.unpack_from("<I", body)[0])
            return
        if obj == self.registry and opcode == 0:  # global(uint name, string interface, uint version)
            name, = struct.unpack_from("<I", body)
            interface = read_string(body, 4)
            self.globals[interface] = (name, None)
        elif obj == self.waiting and opcode == 0:  # wl_callback.done
            self.waiting = None
        elif obj == self.settling and opcode == 0:  # wl_callback.done: our latest set_selection has been taken
            self.settling = None
            self.report(ours=True)
        elif obj == self.device and opcode == 0:  # data_offer(new_id): a new offer, its types to follow
            self.offers[struct.unpack_from("<I", body)[0]] = []
        elif obj == self.device and opcode == 1:  # selection(object or null)
            offer, = struct.unpack_from("<I", body)
            if offer != self.selection:
                self.drop(self.selection)
            self.selection = offer or None
            # Our own set's selection arrives before its sync is done, and is reported then. One arriving with no set
            # of ours under way is another client's (or the selection gone), whether or not the compositor has sent our
            # source's cancel yet: never ours
            if self.settling is None:
                self.current = None
                self.report(ours=False)
        elif obj == self.device and opcode == 2:  # finished: the device is gone, a new one is had
            print("hydratest-portal: the data-control device finished; getting another", file=sys.stderr)
            self.send(self.device, 1, b"")  # destroy
            self.drop(self.selection)
            self.selection = None
            self.get_device()
        elif obj == self.device and opcode == 3:  # primary_selection(object or null): not the clipboard
            offer, = struct.unpack_from("<I", body)
            if offer and offer != self.selection:
                self.drop(offer)
        elif obj in self.offers and opcode == 0:  # offer(string mime_type)
            if (mime := read_string(body, 0)) not in self.offers[obj]:
                self.offers[obj].append(mime)
        elif (obj in self.sources or obj in self.dead_sources) and opcode == 0:  # send(string mime_type, fd)
            # the fd came with this event: taken off the queue whatever its source, so the next paste gets its own
            fd = self.fds.pop(0)
            if obj in self.sources:
                self.on_send(self.sources[obj], read_string(body, 0), fd)
            else:
                os.close(fd)
        elif obj in self.sources and opcode == 1:  # cancelled: another client took the selection
            self.destroy_source(obj)

    def drop(self, offer):
        if offer is not None and self.offers.pop(offer, None) is not None:
            self.send(offer, 1, b"")  # destroy


def wl_string(text):
    raw = text.encode() + b"\0"
    return struct.pack("<I", len(raw)) + raw + b"\0" * (-len(raw) % 4)


def read_string(body, offset):
    length, = struct.unpack_from("<I", body, offset)
    return body[offset + 4:offset + 4 + length - 1].decode()


class Backend:
    def __init__(self):
        self.sessions = {}
        self.zone_set = 1
        self.kclip_serial = 0
        self.kclip_pending = {}  # serial -> the fd a paste reads from, until SelectionWriteDone
        self.kclip = None
        self.kclip_announced = False  # whether a clipboard session has been told the selection yet
        self.kclip_last = None  # (owner, types) the data-control device reported last
        # the latest selection owner change the compositor reported, for Control.Selection: tests wait on it
        # whether or not hydra listens yet
        self.sel_serial = 0
        self.sel_owns = False
        self.sel_mimes = []
        self.sel_reported = set()  # mutter sessions that reported the latest change
        self.held_mods = set()  # evdev codes of the modifiers the hands hold
        self.active = {}  # evdev code of a held trigger key -> [(session, shortcut id)] it activated
        self.captures = set()  # input-capture sessions capturing now

    # Dispatch: method M of interface org.freedesktop.impl.portal.I calls self.I_M.
    def on_call(self, conn, sender, path, iface, method, params, inv):
        fn = getattr(self, f"{iface.rsplit('.', 1)[1]}_{method}", None)
        if fn is None:
            inv.return_dbus_error("org.freedesktop.DBus.Error.NotSupported", f"{method} not supported")
            return
        args = params.unpack()
        request = None
        if params.get_type_string().startswith("(oo"):  # (handle, session_handle, ...)
            request = BUS.register_object(args[0], REQUEST, lambda *a: a[-1].return_value(None), None, None)
        try:
            ret = fn(*args)
        except Exception as e:
            traceback.print_exc()
            inv.return_dbus_error("org.freedesktop.portal.Error.Failed", str(e))
            return
        finally:
            if request:
                BUS.unregister_object(request)
        if isinstance(ret, GLib.Variant):
            inv.return_value(ret)
        elif isinstance(ret, dict):  # (u response, a{sv} results)
            inv.return_value(GLib.Variant("(ua{sv})", (0, ret)))
        elif isinstance(ret, tuple):  # (variant, UnixFDList)
            inv.return_value_with_unix_fd_list(*ret)
        else:
            inv.return_value(None)

    def get_property(self, conn, sender, path, iface, prop):
        return u(PROPS[iface.rsplit(".", 1)[1]][prop])

    def emit(self, iface, name, sig, args):
        BUS.emit_signal(None, OBJ, IMPL + iface, name, GLib.Variant(sig, args))

    def subscribe(self, s, path, handler, iface=None):
        sid = BUS.signal_subscribe(None, iface, None, path, None, Gio.DBusSignalFlags.NONE,
                                   lambda *a: handler(s, a[4], a[5].unpack()))
        s.subs.append(sid)

    def close(self, s, closed_by_compositor):
        if self.sessions.pop(s.handle, None) is None:
            return
        self.captures.discard(s.handle)
        if self.kclip:
            self.kclip.closed(s.handle)
        for sid in s.subs:
            BUS.signal_unsubscribe(sid)
        if closed_by_compositor:
            BUS.emit_signal(None, s.handle, IMPL + "Session", "Closed", None)
        else:
            try:
                if MUTTER and s.kind == "ic" and s.path:
                    call(MIC, s.path, MIC + ".Session", "Close")
                if MUTTER and s.clip:
                    call(MRD, s.clip, MRD + ".Session", "Stop")
                if not MUTTER and s.kind == "ic" and s.path:
                    call(KWIN, "/org/kde/KWin/EIS/InputCapture", KIC + "Manager", "removeInputCapture",
                         GLib.Variant("(o)", (s.path,)))
                for cookie in s.cookies:
                    call(KWIN, "/org/kde/KWin/EIS/RemoteDesktop", KRD, "disconnect", GLib.Variant("(i)", (cookie,)))
            except GLib.Error as e:
                print(f"hydratest-portal: closing {s.handle}: {e.message}", file=sys.stderr)
        BUS.unregister_object(s.reg)

    # ---- InputCapture ----

    def InputCapture_CreateSession(self, handle, session_handle, app_id, parent_window, options):
        s = Session(self, session_handle, "ic")
        caps = self.ic_start(s, options)
        return {"session_id": GLib.Variant("s", session_handle), "capabilities": u(caps)}

    def InputCapture_CreateSession2(self, session_handle, app_id, options):
        Session(self, session_handle, "ic")
        return GLib.Variant("(a{sv})", ({},))  # no response code in CreateSession2

    def InputCapture_Start(self, handle, session_handle, app_id, parent_window, options):
        s = self.sessions[session_handle]
        results = {"capabilities": u(self.ic_start(s, options))}
        if s.clipboard:
            results["clipboard_enabled"] = GLib.Variant("b", True)
        if options.get("persist_mode", 0):
            results["restore_data"] = restore_data(CAPS)
        return results

    def ic_start(self, s, options):
        caps = options.get("capabilities", CAPS) & CAPS
        if MUTTER:
            (s.path,) = call(MIC, "/org/gnome/Mutter/InputCapture", MIC, "CreateSession",
                             GLib.Variant("(u)", (caps,)), "(o)")
            self.subscribe(s, s.path, self.on_mutter_ic)
            if s.clipboard:  # mutter's input capture has no clipboard; borrow a remote-desktop session
                s.clip = self.mutter_rd_session(s)
                call(MRD, s.clip, MRD + ".Session", "Start")
                self.mutter_enable_clipboard(s)
        else:
            (s.path,) = call(KWIN, "/org/kde/KWin/EIS/InputCapture", KIC + "Manager", "addInputCapture",
                             GLib.Variant("(u)", (caps,)), "(o)")
            self.subscribe(s, s.path, self.on_kwin_ic)
            self.kwin_enable_clipboard(s)
        s.started = True
        return caps

    def InputCapture_GetZones(self, handle, session_handle, app_id, options):
        s = self.sessions[session_handle]
        if MUTTER:
            s.mserial, zones = call(MIC, s.path, MIC + ".Session", "GetZones", None, "(ua(uuii))")
        else:
            zones = kwin_zones()
        return {"zones": GLib.Variant("a(uuii)", zones), "zone_set": u(self.zone_set)}

    def InputCapture_SetPointerBarriers(self, handle, session_handle, app_id, options, barriers, zone_set):
        s = self.sessions[session_handle]
        s.barriers, failed = {}, []
        if MUTTER:
            call(MIC, s.path, MIC + ".Session", "ClearBarriers")
        for b in barriers:
            if MUTTER:
                try:
                    (mid,) = call(MIC, s.path, MIC + ".Session", "AddBarrier",
                                  GLib.Variant("(u(iiii))", (s.mserial, tuple(b["position"]))), "(u)")
                    s.barriers[mid] = b["barrier_id"]
                except GLib.Error:
                    failed.append(b["barrier_id"])
            else:
                pos = kwin_barrier(*b["position"], kwin_zones())
                if pos:
                    s.barriers[b["barrier_id"]] = pos
                else:
                    failed.append(b["barrier_id"])
        return {"failed_barriers": GLib.Variant("au", failed)}

    def InputCapture_Enable(self, session_handle, app_id, options):
        s = self.sessions[session_handle]
        if MUTTER:
            call(MIC, s.path, MIC + ".Session", "Enable")
        else:
            barriers = [(bid, (x1, y1), (x2, y2)) for bid, (x1, y1, x2, y2) in s.barriers.items()]
            call(KWIN, s.path, KIC, "enable", GLib.Variant("(a(u(ii)(ii)))", (barriers,)))
        return {}

    def InputCapture_Disable(self, session_handle, app_id, options):
        s = self.sessions[session_handle]
        if MUTTER:
            call(MIC, s.path, MIC + ".Session", "Disable")
        else:
            call(KWIN, s.path, KIC, "disable")
        return {}

    def InputCapture_Release(self, session_handle, app_id, options):
        s = self.sessions[session_handle]
        self.captures.discard(session_handle)
        pos = options.get("cursor_position")
        if MUTTER:
            opts = {"cursor_position": GLib.Variant("(dd)", pos)} if pos else {}
            call(MIC, s.path, MIC + ".Session", "Release", GLib.Variant("(a{sv})", (opts,)))
        else:
            call(KWIN, s.path, KIC, "release", GLib.Variant("((dd)b)", (pos or (0.0, 0.0), pos is not None)))
        return {}

    def InputCapture_ConnectToEIS(self, session_handle, app_id, options):
        s = self.sessions[session_handle]
        if MUTTER:
            return call_fd(MIC, s.path, MIC + ".Session", "ConnectToEIS")
        return call_fd(KWIN, s.path, KIC, "connectToEIS")

    def emit_ic(self, s, name, opts):
        # a compositor fires no shortcut for keys an input capture takes
        if name == "Activated":
            self.captures.add(s.handle)
        elif name in ("Deactivated", "Disabled"):
            self.captures.discard(s.handle)
        self.emit("InputCapture", name, "(oa{sv})", (s.handle, opts))

    def on_mutter_ic(self, s, name, args):
        if name == "Activated":
            barrier, activation, pos = args
            self.emit_ic(s, name, {"activation_id": u(activation), "cursor_position": GLib.Variant("(dd)", pos),
                                   "barrier_id": u(s.barriers.get(barrier, 0))})
        elif name == "Deactivated":
            self.emit_ic(s, name, {"activation_id": u(args[0])})
        elif name == "Disabled":
            self.emit_ic(s, name, {})
        elif name == "ZonesChanged":
            self.zone_set += 1
            self.emit_ic(s, name, {"zone_set": u(self.zone_set)})
        elif name == "Closed":
            self.close(s, closed_by_compositor=True)

    def on_kwin_ic(self, s, name, args):
        if name == "activated":
            activation, barrier, pos = args
            self.emit_ic(s, "Activated", {"activation_id": u(activation), "barrier_id": u(barrier),
                                          "cursor_position": GLib.Variant("(dd)", pos)})
        elif name == "deactivated":
            self.emit_ic(s, "Deactivated", {"activation_id": u(args[0])})
        elif name == "disabled":
            self.emit_ic(s, "Disabled", {})

    def on_monitors_changed(self, *_):
        self.zone_set += 1
        for s in list(self.sessions.values()):
            if s.kind == "ic" and s.started:
                self.emit_ic(s, "ZonesChanged", {"zone_set": u(self.zone_set)})

    # ---- RemoteDesktop ----

    def RemoteDesktop_CreateSession(self, handle, session_handle, app_id, options):
        s = Session(self, session_handle, "rd")
        if MUTTER:
            s.path = s.clip = self.mutter_rd_session(s)
        return {"session_id": GLib.Variant("s", session_handle)}

    def RemoteDesktop_SelectDevices(self, handle, session_handle, app_id, options):
        s = self.sessions[session_handle]
        s.devices = options.get("types", CAPS) & CAPS
        s.persist = options.get("persist_mode", 0)
        return {}

    def RemoteDesktop_Start(self, handle, session_handle, app_id, parent_window, options):
        s = self.sessions[session_handle]
        if MUTTER:
            call(MRD, s.path, MRD + ".Session", "Start")
            if s.clipboard:
                self.mutter_enable_clipboard(s)
        else:
            self.kwin_enable_clipboard(s)
        s.started = True
        results = {"devices": u(s.devices)}
        if s.clipboard:
            results["clipboard_enabled"] = GLib.Variant("b", True)
        if options.get("persist_mode", s.persist):
            results["restore_data"] = restore_data(s.devices)
        return results

    def RemoteDesktop_ConnectToEIS(self, session_handle, app_id, options):
        s = self.sessions[session_handle]
        if MUTTER:
            return call_fd(MRD, s.path, MRD + ".Session", "ConnectToEIS",
                           GLib.Variant("(a{sv})", ({"device-types": u(s.devices)},)))
        res, fds = call_fd(KWIN, "/org/kde/KWin/EIS/RemoteDesktop", KRD, "connectToEIS",
                           GLib.Variant("(i)", (s.devices,)), "(hi)")
        index, cookie = res.unpack()
        s.cookies.append(cookie)
        return GLib.Variant("(h)", (index,)), fds

    def mutter_rd_session(self, s):
        (path,) = call(MRD, "/org/gnome/Mutter/RemoteDesktop", MRD, "CreateSession", None, "(o)")
        self.subscribe(s, path, self.on_mutter_rd, MRD + ".Session")
        return path

    def on_mutter_rd(self, s, name, args):
        if name == "SelectionOwnerChanged":
            mimes = args[0].get("mime-types", [])
            if isinstance(mimes, tuple):
                (mimes,) = mimes
            owns = args[0].get("session-is-owner", False)
            self.mutter_selection(s, list(mimes), owns)
            opts = {"mime_types": GLib.Variant("as", mimes),
                    "session_is_owner": GLib.Variant("b", owns)}
            self.emit("Clipboard", name, "(oa{sv})", (s.handle, opts))
        elif name == "SelectionTransfer":
            self.emit("Clipboard", name, "(osu)", (s.handle, *args))
        elif name == "Closed":
            self.close(s, closed_by_compositor=True)

    def mutter_selection(self, s, mimes, owns):
        """mutter tells each clipboard session of one owner change apart: a session reporting again, or other types,
        is the next change, else it is the change another session already reported, owned if any session owns it."""
        if s.handle in self.sel_reported or mimes != self.sel_mimes or not self.sel_reported:
            self.record_selection(owns, mimes)
            self.sel_reported = {s.handle}
        else:
            self.sel_owns = self.sel_owns or owns
            self.sel_reported.add(s.handle)

    def record_selection(self, owns, mimes):
        self.sel_serial += 1
        self.sel_owns = owns
        self.sel_mimes = mimes
        self.sel_reported = set()

    # ---- Clipboard ----

    def Clipboard_RequestClipboard(self, session_handle, options):
        self.sessions[session_handle].clipboard = True

    def Clipboard_SetSelection(self, session_handle, options):
        s = self.sessions[session_handle]
        mimes = options.get("mime_types", [])
        if MUTTER:
            call(MRD, s.clip, MRD + ".Session", "SetSelection",
                 GLib.Variant("(a{sv})", ({"mime-types": GLib.Variant("as", mimes)},)))
            return
        # as xdg-desktop-portal-kde: a data-control source offering the session's types, each paste asked of the
        # session as it comes (SelectionTransfer), however long after the call; the frontend refuses the call
        # itself while the session's capture is off
        self.kclip.set_selection(s.handle, mimes)

    def on_kwin_send(self, owner, mime, fd):
        """A client pastes the session's selection: the session is asked for it, and answers into fd."""
        self.kclip_serial += 1
        self.kclip_pending[self.kclip_serial] = fd
        self.emit("Clipboard", "SelectionTransfer", "(osu)", (owner, mime, self.kclip_serial))

    def Clipboard_SelectionWrite(self, session_handle, serial):
        s = self.sessions[session_handle]
        if MUTTER:
            return call_fd(MRD, s.clip, MRD + ".Session", "SelectionWrite", GLib.Variant("(u)", (serial,)))
        if serial not in self.kclip_pending:
            raise ValueError(f"no pending transfer with serial {serial}")
        return fd_reply(os.dup(self.kclip_pending[serial]))

    def Clipboard_SelectionWriteDone(self, session_handle, serial, success):
        s = self.sessions[session_handle]
        if MUTTER:
            call(MRD, s.clip, MRD + ".Session", "SelectionWriteDone", GLib.Variant("(ub)", (serial, success)))
            return
        fd = self.kclip_pending.pop(serial, None)
        if fd is None:
            raise ValueError(f"no pending transfer with serial {serial}")
        os.close(fd)

    def Clipboard_SelectionRead(self, session_handle, mime_type):
        s = self.sessions[session_handle]
        if MUTTER:
            return call_fd(MRD, s.clip, MRD + ".Session", "SelectionRead", GLib.Variant("(s)", (mime_type,)))
        # as xdg-desktop-portal-kde: the current offer asked for the type straight through the data-control device,
        # refused when the offer lacks it
        r = self.kclip.receive(mime_type) if self.kclip else None
        if r is None:
            raise ValueError("requesting not offered format")
        return fd_reply(r)

    def mutter_enable_clipboard(self, s):
        call(MRD, s.clip, MRD + ".Session", "EnableClipboard", GLib.Variant("(a{sv})", ({},)))

    def kwin_enable_clipboard(self, s):
        if not s.clipboard or self.kclip_announced:
            return
        # the data-control client runs from the start; the first clipboard session hears the selection it holds, as
        # it did when the client was made for it, without counting it as a change
        self.kclip_announced = True
        if self.kclip_last is not None:
            # after the caller marks the session started, as the new client's first report came
            GLib.idle_add(lambda: self.announce_kwin_selection(*self.kclip_last) and False)

    def on_kwin_selection(self, owner, types):
        """The data-control device reported a selection: the session owns it when it is that session's source."""
        self.kclip_last = (owner, types)
        self.record_selection(owner is not None, list(types))
        self.announce_kwin_selection(owner, types)

    def announce_kwin_selection(self, owner, types):
        for s in list(self.sessions.values()):
            if s.clipboard and s.started:
                opts = {"mime_types": GLib.Variant("as", types),
                        "session_is_owner": GLib.Variant("b", s.handle == owner)}
                self.emit("Clipboard", "SelectionOwnerChanged", "(oa{sv})", (s.handle, opts))


    # ---- GlobalShortcuts (same for both compositors; triggered through org.hydratest.Control) ----

    def GlobalShortcuts_CreateSession(self, handle, session_handle, app_id, options):
        Session(self, session_handle, "gs")
        return {"session_id": GLib.Variant("s", session_handle)}

    def GlobalShortcuts_BindShortcuts(self, handle, session_handle, shortcuts, parent_window, options):
        s = self.sessions[session_handle]
        old = s.shortcuts
        s.shortcuts = {sid: (o.get("description", ""), o.get("preferred_trigger", "")) for sid, o in shortcuts}
        if old and old != s.shortcuts:
            BUS.emit_signal(None, OBJ, IMPL + "GlobalShortcuts", "ShortcutsChanged",
                            GLib.Variant("(oa(sa{sv}))", (s.handle, self.shortcut_list(s))))
        return {"shortcuts": GLib.Variant("a(sa{sv})", self.shortcut_list(s))}

    def GlobalShortcuts_ListShortcuts(self, handle, session_handle):
        return {"shortcuts": GLib.Variant("a(sa{sv})", self.shortcut_list(self.sessions[session_handle]))}

    def shortcut_list(self, s):
        return [(sid, {"description": GLib.Variant("s", desc), "trigger_description": GLib.Variant("s", trig)})
                for sid, (desc, trig) in s.shortcuts.items()]

    def fire(self, s, shortcut_id, pressed):
        name = "Activated" if pressed else "Deactivated"
        now = GLib.get_monotonic_time() // 1000
        self.emit("GlobalShortcuts", name, "(osta{sv})", (s.handle, shortcut_id, now, {}))

    # a key the user's hands pressed or released, as the compositor saw it: a key going down with exactly a bound
    # trigger's modifiers held activates that shortcut, as GNOME's and KDE's own portals do, and its release ends it
    def on_key(self, code, pressed):
        if code in MODIFIER_KEYS:
            if pressed:
                self.held_mods.add(code)
            else:
                self.held_mods.discard(code)
            return
        name = KEY_NAMES.get(code)
        if not pressed:
            for s, sid in self.active.pop(code, []):
                if s.handle in self.sessions:
                    self.fire(s, sid, False)
            return
        if name is None or code in self.active or self.captures:
            return
        mods = {MODIFIER_KEYS[m] for m in self.held_mods}
        hits = [(s, sid) for s in self.sessions.values() for sid, (_, trig) in s.shortcuts.items()
                if parse_trigger(trig) == (mods, name.lower())]
        if hits:
            self.active[code] = hits
            for s, sid in hits:
                self.fire(s, sid, True)

    # ---- org.hydratest.Control (test-only) ----

    def on_control(self, conn, sender, path, iface, method, params, inv):
        if method == "Shortcuts":
            bound = [(sid, trig) for s in self.sessions.values() for sid, (_, trig) in s.shortcuts.items()]
            inv.return_value(GLib.Variant("(a(ss))", (bound,)))
            return
        if method == "Key":
            self.on_key(*params.unpack())
            inv.return_value(None)
            return
        if method == "Selection":
            inv.return_value(GLib.Variant("(ubas)", (self.sel_serial, self.sel_owns, self.sel_mimes)))
            return
        shortcut_id, pressed = params.unpack()
        targets = [s for s in self.sessions.values() if shortcut_id in s.shortcuts]
        if not targets:
            inv.return_dbus_error("org.hydratest.Error.NotBound", f"no session bound {shortcut_id}")
            return
        for s in targets:
            self.fire(s, shortcut_id, pressed)
        inv.return_value(None)


# the evdev modifier keys, as the shortcuts spec names them in a trigger
MODIFIER_KEYS = {29: "CTRL", 97: "CTRL", 56: "ALT", 42: "SHIFT", 54: "SHIFT", 125: "LOGO", 126: "LOGO"}
# the keysym names of the other evdev keys on a us layout
KEY_NAMES = {
    **{16 + i: c for i, c in enumerate("qwertyuiop")},
    **{30 + i: c for i, c in enumerate("asdfghjkl")},
    **{44 + i: c for i, c in enumerate("zxcvbnm")},
    **{2 + i: str((i + 1) % 10) for i in range(10)},
    **{59 + i: f"F{i + 1}" for i in range(10)},
    1: "Escape", 14: "BackSpace", 15: "Tab", 28: "Return", 57: "space", 70: "Scroll_Lock", 87: "F11", 88: "F12",
    99: "Print", 102: "Home", 103: "Up", 104: "Prior", 105: "Left", 106: "Right", 107: "End", 108: "Down",
    109: "Next", 110: "Insert", 111: "Delete",
}


def parse_trigger(trigger):
    """A preferred_trigger such as CTRL+ALT+LOGO+l as (modifiers, lowercased key); None for none."""
    parts = trigger.split("+") if trigger else []
    return ({p.upper() for p in parts[:-1]}, parts[-1].lower()) if parts else None


CONTROL_XML = """<node><interface name="org.hydratest.Control">
  <method name="Trigger"><arg type="s" name="shortcut_id"/><arg type="b" name="pressed"/></method>
  <method name="Shortcuts"><arg type="a(ss)" name="shortcuts" direction="out"/></method>
  <method name="Key"><arg type="u" name="evdev_code"/><arg type="b" name="pressed"/></method>
  <method name="Selection"><arg type="u" name="serial" direction="out"/><arg type="b" name="session_owns" direction="out"/>
    <arg type="as" name="mime_types" direction="out"/></method>
</interface></node>"""

if COMPOSITOR not in ("mutter", "kwin"):
    sys.exit("hydratest-portal: set HYDRATEST_COMPOSITOR to mutter or kwin")
SESSION = iface_info("Session")
REQUEST = iface_info("Request")
BACKEND = Backend()
if not MUTTER:
    # watching from the start, so Control.Selection sees a copy made before any session asks for the clipboard
    BACKEND.kclip = DataControl(BACKEND.on_kwin_send, BACKEND.on_kwin_selection)
GDK = None
if not MUTTER and not os.environ.get("HYDRATEST_ZONES"):
    import gi

    gi.require_version("Gdk", "4.0")
    from gi.repository import Gdk

    GDK = Gdk.Display.open(os.environ.get("WAYLAND_DISPLAY"))
    GDK.get_monitors().connect("items-changed", BACKEND.on_monitors_changed)
for name in ("InputCapture", "RemoteDesktop", "Clipboard", "GlobalShortcuts"):
    BUS.register_object(OBJ, iface_info(name), BACKEND.on_call, BACKEND.get_property, None)
BUS.register_object("/org/hydratest/Control", Gio.DBusNodeInfo.new_for_xml(CONTROL_XML).interfaces[0],
                    BACKEND.on_control, None, None)
(owned,) = call("org.freedesktop.DBus", "/org/freedesktop/DBus", "org.freedesktop.DBus", "RequestName",
                GLib.Variant("(su)", (NAME, 4)), "(u)")  # 4 = DO_NOT_QUEUE
if owned != 1:
    sys.exit(f"hydratest-portal: could not own {NAME}")
GLib.MainLoop().run()

#!/usr/bin/env python3
"""Hydra integration-test probe: shows what a desktop application receives.

Opens one undecorated fullscreen window on the first monitor and prints one JSON
object per line to stdout for every input event. Reads JSON commands from stdin
(clipboard, primary selection, present, quit) and answers each with one line.
Requires python3-gobject and GTK 4; no pip packages.
"""
import base64
import json
import os
import sys

import gi

gi.require_version("Gdk", "4.0")
gi.require_version("Gtk", "4.0")
from gi.repository import Gdk, Gio, GLib, Gtk  # noqa: E402

LEVEL3_KEYS = {"ISO_Level3_Shift", "Mode_switch"}  # AltGr keysyms


def emit(obj):
    try:
        sys.stdout.write(json.dumps(obj) + "\n")
        sys.stdout.flush()
    except BrokenPipeError:
        pass


class Probe:
    def __init__(self, app):
        self.app = app
        self.level3_held = set()
        self.stdin_buf = b""
        display = Gdk.Display.get_default()
        self.clipboard = display.get_clipboard()
        self.primary = display.get_primary_clipboard()

        win = self.win = Gtk.ApplicationWindow(application=app, title="hydra-input-probe")
        win.set_decorated(False)
        monitor = display.get_monitors().get_item(0)
        if monitor is not None:
            geo = monitor.get_geometry()
            win.set_default_size(geo.width, geo.height)  # for WMs that ignore fullscreen
            win.fullscreen_on_monitor(monitor)
        else:
            win.fullscreen()

        # The child's "resize" fires on every allocation change.
        area = Gtk.DrawingArea(hexpand=True, vexpand=True)
        area.connect("resize", lambda *_: self.report_size())
        win.set_child(area)
        win.connect("map", lambda *_: self.report_size())
        win.connect("notify::is-active", lambda w, _p: emit({"focus": w.is_active()}))

        motion = Gtk.EventControllerMotion()
        motion.connect("motion", lambda _c, x, y: emit({"motion": [x, y]}))
        motion.connect("enter", lambda _c, x, y: emit({"enter": [x, y]}))
        motion.connect("leave", lambda _c: emit({"leave": True}))
        win.add_controller(motion)

        # Buttons via the legacy controller: a GestureClick cancels itself once the
        # pointer moves past the drag threshold, which would hide releases after drags.
        legacy = Gtk.EventControllerLegacy()
        legacy.connect("event", self.on_event)
        win.add_controller(legacy)

        # No DISCRETE flag, so smooth (fractional) deltas come through unchanged.
        scroll = Gtk.EventControllerScroll.new(Gtk.EventControllerScrollFlags.BOTH_AXES)
        scroll.connect("scroll", self.on_scroll)
        win.add_controller(scroll)

        keys = Gtk.EventControllerKey()
        keys.set_propagation_phase(Gtk.PropagationPhase.CAPTURE)
        keys.connect("key-pressed", lambda _c, kv, _code, st: self.on_key(kv, st, True))
        keys.connect("key-released", lambda _c, kv, _code, st: self.on_key(kv, st, False))
        win.add_controller(keys)

        GLib.io_add_watch(GLib.IOChannel.unix_new(0), GLib.PRIORITY_DEFAULT,
                          GLib.IOCondition.IN | GLib.IOCondition.HUP | GLib.IOCondition.ERR,
                          self.on_stdin)
        win.present()

    def report_size(self):
        w, h = self.win.get_width(), self.win.get_height()
        if self.win.get_mapped() and w > 0 and h > 0:
            emit({"ready": True, "width": w, "height": h})

    # ---- input events ----

    def on_event(self, ctrl, event):
        # some PyGObject builds hand the legacy controller's event in as None
        event = event or ctrl.get_current_event()
        if event is None:
            return False
        kind = event.get_event_type()
        if kind in (Gdk.EventType.BUTTON_PRESS, Gdk.EventType.BUTTON_RELEASE):
            _ok, x, y = event.get_position()  # surface coordinates
            dx, dy = self.win.get_surface_transform()  # surface -> window offset
            emit({"button": event.get_button(),
                  "pressed": kind == Gdk.EventType.BUTTON_PRESS,
                  "at": [x - dx, y - dy]})
        return False

    def on_scroll(self, ctrl, dx, dy):
        unit = "wheel"
        if hasattr(ctrl, "get_unit") and ctrl.get_unit() == Gdk.ScrollUnit.SURFACE:  # GTK >= 4.8
            unit = "surface"
        emit({"scroll": [dx, dy], "unit": unit})
        return True

    def on_key(self, keyval, state, pressed):
        name = Gdk.keyval_name(keyval) or ""
        if name in LEVEL3_KEYS:
            (self.level3_held.add if pressed else self.level3_held.discard)(name)
        code = Gdk.keyval_to_unicode(keyval)
        mods = [label for label, mask in (("shift", Gdk.ModifierType.SHIFT_MASK),
                                          ("control", Gdk.ModifierType.CONTROL_MASK),
                                          ("alt", Gdk.ModifierType.ALT_MASK),
                                          ("super", Gdk.ModifierType.SUPER_MASK))
                if state & mask]
        if self.level3_held:  # GTK 4 has no AltGr modifier bit; track the key itself
            mods.append("altgr")
        emit({"key": name, "char": chr(code) if code else "", "pressed": pressed, "mods": mods})
        return pressed  # swallow presses so GTK keybindings do not act on them

    # ---- stdin commands ----

    def on_stdin(self, _source, _cond):
        try:
            chunk = os.read(0, 65536)
        except OSError:
            chunk = b""
        if not chunk:  # EOF: the test harness went away
            self.app.quit()
            return False
        self.stdin_buf += chunk
        while b"\n" in self.stdin_buf:
            line, self.stdin_buf = self.stdin_buf.split(b"\n", 1)
            if line.strip():
                self.handle_line(line)
        return True

    def handle_line(self, line):
        try:
            cmd = json.loads(line)
            if not isinstance(cmd, dict):
                raise ValueError("command must be a JSON object")
            self.dispatch(cmd)
        except Exception as e:  # noqa: BLE001 - report, never crash
            emit({"error": f"{type(e).__name__}: {e}"})

    def dispatch(self, cmd):
        if "copy" in cmd:
            self.clipboard.set(str(cmd["copy"]))
            emit({"copied": cmd["copy"]})
        elif "copy_png_base64" in cmd:
            data = base64.b64decode(cmd["copy_png_base64"])
            # offered as the PNG bytes themselves: a provider made from a texture value has the concrete texture type,
            # which GTK's image serializers don't match, so it would offer no image format at all
            self.clipboard.set_content(Gdk.ContentProvider.new_for_bytes("image/png", GLib.Bytes.new(data)))
            emit({"copied_png": True})
        elif "paste" in cmd:
            self.read_text(self.clipboard, "pasted")
        elif "paste_png" in cmd:
            self.clipboard.read_texture_async(None, self.on_texture)
        elif "formats" in cmd:
            mimes = self.clipboard.get_formats().get_mime_types()
            if isinstance(mimes, tuple):  # some PyGObject versions also return the length
                mimes = mimes[0]
            emit({"formats": list(mimes or [])})
        elif "select" in cmd:
            self.primary.set(str(cmd["select"]))
            emit({"selected": cmd["select"]})
        elif "paste_primary" in cmd:
            self.read_text(self.primary, "pasted_primary")
        elif "present" in cmd:
            self.win.present()
            emit({"presented": True})
        elif "quit" in cmd:
            self.app.quit()
        else:
            raise ValueError(f"unknown command: {sorted(cmd)}")

    def read_text(self, clipboard, reply_key):
        def done(cb, result):
            try:
                emit({reply_key: cb.read_text_finish(result)})
            except GLib.Error:
                emit({reply_key: None})
            except Exception as e:  # noqa: BLE001
                emit({"error": f"{type(e).__name__}: {e}"})

        clipboard.read_text_async(None, done)

    def on_texture(self, cb, result):
        try:
            texture = cb.read_texture_finish(result)
            png = texture.save_to_png_bytes().get_data() if texture else None
            emit({"pasted_png_base64": base64.b64encode(png).decode() if png else None})
        except GLib.Error:
            emit({"pasted_png_base64": None})
        except Exception as e:  # noqa: BLE001
            emit({"error": f"{type(e).__name__}: {e}"})


def main():
    app = Gtk.Application(application_id="no.hydra.InputProbe",
                          flags=Gio.ApplicationFlags.NON_UNIQUE)
    probes = []
    app.connect("activate", lambda a: probes.append(Probe(a)))
    return app.run([sys.argv[0]])


if __name__ == "__main__":
    sys.exit(main())

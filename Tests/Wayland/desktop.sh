#!/usr/bin/env bash
# Starts ONE headless desktop in its own private session and prints the env a client of it needs.
#
#   desktop.sh <gnome|kde|openbox|kde-x11|hyprland> <name> [monitors]   monitors: "WxH[,WxH...]", default 1920x1080
#   desktop.sh stop <name>
#
# stdout is only the client env, one KEY=VALUE per line; every child logs to /tmp/hd/<name>/*.log.
# The root is short because Hyprland's IPC socket, $ROOT/<name>/hypr/<63-char signature>/.socket.sock,
# must fit the 107-character unix socket path limit.
# An X11 desktop prints no WAYLAND_DISPLAY, and its client must not inherit one.
# hyprland is not headless: it drives the machine's DRM device and seat (the VM Tests/Wayland/vm/vm.sh boots),
# so a container runs one at a time, on the one monitor that device has.
set -euo pipefail

ROOT=/tmp/hd
TIMEOUT=30

die() { echo "desktop.sh: $*" >&2; exit 1; }
usage() { die "usage: desktop.sh <gnome|kde|openbox|kde-x11|hyprland> <name> [monitors] | desktop.sh stop <name>"; }

stop() {
	local pids="$ROOT/$1/pids" pid deadline
	[ -f "$pids" ] || return 0
	tac "$pids" | while read -r pid; do kill "$pid" 2>/dev/null || true; done
	deadline=$((SECONDS + TIMEOUT))
	while read -r pid; do
		while kill -0 "$pid" 2>/dev/null; do
			if [ "$SECONDS" -ge "$deadline" ]; then kill -9 "$pid" 2>/dev/null || true; break; fi
			sleep 0.1
		done
	done < "$pids"
	rm -f "$pids"
}

[ $# -ge 2 ] || usage
if [ "$1" = stop ]; then stop "$2"; exit 0; fi

KIND="$1" NAME="$2" MONITORS="${3:-1920x1080}"
case "$KIND" in gnome|kde|openbox|kde-x11|hyprland) ;; *) usage ;; esac
[[ "$NAME" =~ ^[A-Za-z0-9_-]+$ ]] || die "name '$NAME' must be letters, digits, - or _"
[[ "$MONITORS" =~ ^[0-9]+x[0-9]+(,[0-9]+x[0-9]+)*$ ]] || die "monitors '$MONITORS' must look like 1920x1080[,1280x1024]"
IFS=, read -r -a MONS <<< "$MONITORS"

DIR="$ROOT/$NAME"
[ ! -f "$DIR/pids" ] || die "desktop '$NAME' is already running; run 'desktop.sh stop $NAME' first"
rm -rf "$DIR"
mkdir -p "$DIR/home"
chmod 0700 "$DIR"

# a desktop that fails half way up is torn down, so a retry starts clean
started=0
trap '[ "$started" = 1 ] || stop "$NAME"' EXIT

# spawn <log> <cmd...>: run in the background, detached from our stdout so the caller sees EOF
spawn() {
	local log="$DIR/$1.log"; shift
	"$@" < /dev/null > "$log" 2>&1 &
	echo $! >> "$DIR/pids"
	LAST_PID=$!
	LAST_LOG="$log"
}

# wait_for <what> <cmd...>: poll until cmd succeeds, failing if the last spawned process dies or time runs out
wait_for() {
	local what="$1" deadline=$((SECONDS + TIMEOUT)); shift
	until "$@"; do
		if ! kill -0 "$LAST_PID" 2>/dev/null; then
			tail -n 20 "$LAST_LOG" >&2 || true
			die "$KIND '$NAME': process $LAST_PID exited while waiting for $what (log: $LAST_LOG)"
		fi
		[ "$SECONDS" -lt "$deadline" ] || die "$KIND '$NAME': no $what after ${TIMEOUT}s (log: $LAST_LOG)"
		sleep 0.1
	done
}

has_bus_name() {
	local reply
	reply="$(dbus-send --session --print-reply --dest=org.freedesktop.DBus /org/freedesktop/DBus \
		org.freedesktop.DBus.NameHasOwner "string:$1" 2>/dev/null)" || return 1
	[[ "$reply" == *"boolean true"* ]]
}

# the ICCCM manager selection a window manager takes once it is managing screen 0
wm_running() {
	python3 - <<-'PY'
		import ctypes, sys
		x = ctypes.CDLL("libX11.so.6")
		x.XOpenDisplay.restype = ctypes.c_void_p
		x.XInternAtom.argtypes = [ctypes.c_void_p, ctypes.c_char_p, ctypes.c_int]
		x.XInternAtom.restype = ctypes.c_ulong
		x.XGetSelectionOwner.argtypes = [ctypes.c_void_p, ctypes.c_ulong]
		x.XGetSelectionOwner.restype = ctypes.c_ulong
		d = x.XOpenDisplay(None)
		sys.exit(0 if d and x.XGetSelectionOwner(d, x.XInternAtom(d, b"WM_S0", 0)) else 1)
	PY
}

# the first of a distribution's install paths for a libexec binary: Fedora's /usr/libexec, Arch's /usr/lib
libexec() {
	local p
	for p in "/usr/libexec/$1" "/usr/lib/$1"; do [ -x "$p" ] && { echo "$p"; return; }; done
	die "$1 is not installed under /usr/libexec or /usr/lib"
}

hypr_socket() { compgen -G "$XDG_RUNTIME_DIR/hypr/*/.socket.sock" > /dev/null; }

# the compositor's Wayland socket, wayland-<n> in its runtime dir
wayland_socket() {
	local s
	for s in "$XDG_RUNTIME_DIR"/wayland-[0-9]*; do
		[ -S "$s" ] && { basename "$s"; return 0; }
	done
	return 1
}

# the size of the one monitor hyprctl reports, as WxH; fails while it reports none or several
hypr_monitor() {
	hyprctl -j monitors 2>/dev/null | python3 -c '
import json, sys
m = json.load(sys.stdin)
if len(m) != 1:
    sys.exit(1)
print("%dx%d" % (m[0]["width"], m[0]["height"]))'
}
hypr_monitor_is() { [ "$(hypr_monitor)" = "$1" ]; }

export XDG_RUNTIME_DIR="$DIR" HOME="$DIR/home" XDG_CURRENT_DESKTOP=hydratest
# xdg-desktop-portal picks its backends by desktop name, and Hyprland's is its own (hyprland-portals.conf)
if [ "$KIND" = hyprland ]; then XDG_CURRENT_DESKTOP=Hyprland; fi
# the portal watches the applications directory only if it exists when it starts, as it does on a real desktop
mkdir -p "$HOME/.local/share/applications"
unset WAYLAND_DISPLAY DISPLAY DBUS_SESSION_BUS_ADDRESS HYPRLAND_INSTANCE_SIGNATURE
case "$KIND" in gnome|kde|hyprland) export XDG_SESSION_TYPE=wayland ;; *) export XDG_SESSION_TYPE=x11 ;; esac

{ read -r DBUS_SESSION_BUS_ADDRESS || true; read -r bus_pid || true; } < <(dbus-daemon --session --fork --print-address --print-pid)
[ -n "${DBUS_SESSION_BUS_ADDRESS:-}" ] && [ -n "${bus_pid:-}" ] || die "dbus-daemon did not start a session bus"
echo "$bus_pid" >> "$DIR/pids"
export DBUS_SESSION_BUS_ADDRESS

case "$KIND" in
	gnome)
		args=(--wayland --headless --wayland-display "wayland-$NAME")
		# newer mutter builds have no X11 at all, and dropped the flag with it
		if [[ "$(mutter --help 2>&1 || true)" == *--no-x11* ]]; then args+=(--no-x11); fi
		for m in "${MONS[@]}"; do args+=(--virtual-monitor "$m"); done
		spawn mutter mutter "${args[@]}"
		;;
	kde)
		for m in "${MONS[@]}"; do [ "$m" = "${MONS[0]}" ] || die "kde: kwin's virtual outputs share one size, got $MONITORS"; done
		args=(--virtual --no-lockscreen --socket "wayland-$NAME" --width "${MONS[0]%x*}" --height "${MONS[0]#*x}")
		if [ "${#MONS[@]}" -gt 1 ] && [[ "$(kwin_wayland --help 2>&1 || true)" == *--output-count* ]]; then
			args+=(--output-count "${#MONS[@]}")
		else
			MONS=("${MONS[0]}")
		fi
		spawn kwin kwin_wayland "${args[@]}"
		;;
	openbox|kde-x11)
		# Xvfb has one screen, so several monitors become their left-to-right bounding box
		w=0 h=0
		for m in "${MONS[@]}"; do w=$((w + ${m%x*})); h=$((h > ${m#*x} ? h : ${m#*x})); done
		# -displayfd: Xvfb picks a free display itself, so desktops started side by side never race for one
		# created first, so polling it never races the redirect in Xvfb's shell; that redirect only truncates it
		: > "$DIR/display"
		# shellcheck disable=SC2016 # $1 and $2 are the inner shell's arguments
		spawn xvfb bash -c 'exec Xvfb -displayfd 3 -screen 0 "$1"x24 -noreset 3> "$2"' _ "${w}x$h" "$DIR/display"
		wait_for "display number from Xvfb" grep -q '^[0-9]' "$DIR/display"
		DISPLAY=":$(head -n 1 "$DIR/display")"
		export DISPLAY
		wait_for "X socket /tmp/.X11-unix/X${DISPLAY#:}" test -S "/tmp/.X11-unix/X${DISPLAY#:}"
		if [ "$KIND" = openbox ]; then spawn openbox openbox; else spawn kwin kwin_x11 --replace; fi
		wait_for "window manager on $DISPLAY" wm_running
		;;
	hyprland)
		[ "${#MONS[@]}" -eq 1 ] || die "hyprland: drives the machine's one display, got $MONITORS"
		[ -c /dev/dri/card0 ] || die "hyprland: no /dev/dri/card0; it needs a KMS device (run it in the VM Tests/Wayland/vm/vm.sh boots)"
		! pgrep -x 'Hyprland|seatd' > /dev/null || die "hyprland: one is already running in this container, and it holds the DRM device and seat"
		# Hyprland and xdg-desktop-portal-hyprland are slower to come up on software rendering than the headless kinds
		TIMEOUT=60
		# Hyprland takes the seat through seatd, since a container has no logind; a dead one's socket would refuse it
		rm -f /run/seatd.sock
		spawn seatd env SEATD_VTBOUND=0 seatd
		wait_for "seatd socket /run/seatd.sock" test -S /run/seatd.sock
		mkdir -p "$HOME/.config/hypr"
		# xdg-desktop-portal-hyprland ignores a shortcut's preferred_trigger, so Hydra's hotkeys at home fire only once
		# bound here as a user would: its default chords on the ids WaylandShortcuts registers (<action>-<chord index>)
		cat > "$HOME/.config/hypr/hyprland.lua" <<-'LUA'
			-- flat input: the hands' steps must reach hydra as written; the tests measure hydra, not libinput's acceleration
			hl.config({
				misc = { disable_hyprland_logo = true },
				debug = { enable_stdout_logs = true, disable_logs = false },
				input = { accel_profile = "flat", sensitivity = 0 },
			})
			hl.bind("CTRL + ALT + SUPER + L", hl.dsp.global("org.cathedral.Hydra:ToggleCursorLock-0"))
			hl.bind("CTRL + ALT + SUPER + K", hl.dsp.global("org.cathedral.Hydra:LockSlaves-0"))
			hl.bind("CTRL + ALT + SUPER + C", hl.dsp.global("org.cathedral.Hydra:CopyFiles-0"))
			hl.bind("CTRL + ALT + SUPER + V", hl.dsp.global("org.cathedral.Hydra:PasteFiles-0"))
			-- the probes stand in for fullscreen apps; tiled, they get resized and lose focus, so report no pointer leave
			hl.window_rule({ match = { class = "^game-probe$" }, fullscreen = true })
			hl.window_rule({ match = { class = "^no\\.hydra\\.InputProbe$" }, fullscreen = true })
		LUA
		spawn hyprland env LIBSEAT_BACKEND=seatd AQ_DRM_DEVICES=/dev/dri/card0 HYPRLAND_NO_SD_NOTIFY=1 \
			Hyprland --i-am-really-stupid
		wait_for "Hyprland socket $XDG_RUNTIME_DIR/hypr/*/.socket.sock" hypr_socket
		sock="$(compgen -G "$XDG_RUNTIME_DIR/hypr/*/.socket.sock" | head -n 1)"
		HYPRLAND_INSTANCE_SIGNATURE="$(basename "$(dirname "$sock")")"
		export HYPRLAND_INSTANCE_SIGNATURE
		wait_for "Wayland socket in $XDG_RUNTIME_DIR" wayland_socket > /dev/null
		WAYLAND_DISPLAY="$(wayland_socket)"
		export WAYLAND_DISPLAY
		# the monitor is the VM's (qemu's virtio-gpu xres/yres), so a test asking for another size gets told so
		wait_for "Hyprland monitor ${MONS[0]}" hypr_monitor_is "${MONS[0]}"
		;;
esac

if [ "$KIND" = hyprland ]; then
	# started here rather than activated by the portal: an activated one gets the bus's env, which has no WAYLAND_DISPLAY
	xdph="$(libexec xdg-desktop-portal-hyprland)"
	xdp="$(libexec xdg-desktop-portal)"
	spawn xdph "$xdph"
	wait_for "bus name org.freedesktop.impl.portal.desktop.hyprland" has_bus_name org.freedesktop.impl.portal.desktop.hyprland
	spawn xdg-desktop-portal "$xdp"
	wait_for "bus name org.freedesktop.portal.Desktop" has_bus_name org.freedesktop.portal.Desktop
fi

if [ "$KIND" = gnome ] || [ "$KIND" = kde ]; then
	export WAYLAND_DISPLAY="wayland-$NAME"
	wait_for "Wayland socket $DIR/$WAYLAND_DISPLAY" test -S "$DIR/$WAYLAND_DISPLAY"

	if [ "$KIND" = gnome ]; then
		export HYDRATEST_COMPOSITOR=mutter
	else
		export HYDRATEST_COMPOSITOR=kwin
		zones=() x=0
		for m in "${MONS[@]}"; do zones+=("$m+$x+0"); x=$((x + ${m%x*})); done
		HYDRATEST_ZONES="$(IFS=,; echo "${zones[*]}")"
		export HYDRATEST_ZONES
	fi
	spawn portal /usr/libexec/hydratest-portal
	wait_for "bus name org.freedesktop.impl.portal.desktop.hydratest" has_bus_name org.freedesktop.impl.portal.desktop.hydratest
	spawn xdg-desktop-portal /usr/libexec/xdg-desktop-portal
	wait_for "bus name org.freedesktop.portal.Desktop" has_bus_name org.freedesktop.portal.Desktop
fi

started=1
echo "DBUS_SESSION_BUS_ADDRESS=$DBUS_SESSION_BUS_ADDRESS"
echo "XDG_RUNTIME_DIR=$XDG_RUNTIME_DIR"
# the portal finds an app's desktop entry under this home, so its clients must share it
echo "HOME=$HOME"
if [ -n "${WAYLAND_DISPLAY:-}" ]; then echo "WAYLAND_DISPLAY=$WAYLAND_DISPLAY"; else echo "DISPLAY=$DISPLAY"; fi
echo "XDG_SESSION_TYPE=$XDG_SESSION_TYPE"
if [ "$KIND" = hyprland ]; then
	echo "HYPRLAND_INSTANCE_SIGNATURE=$HYPRLAND_INSTANCE_SIGNATURE"
	echo "XDG_CURRENT_DESKTOP=$XDG_CURRENT_DESKTOP"
fi

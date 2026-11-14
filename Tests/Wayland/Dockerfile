FROM fedora:45

# the desktops the tests start in this image; Plasma 6.8 dropped X11, so kde-x11 has its own (Dockerfile.kde-x11)
ENV HYDRA_DESKTOP_KINDS=openbox,gnome,kde,evdev

RUN dnf install -y \
		mutter kwin openbox xorg-x11-server-Xvfb \
		xdg-desktop-portal libei libei-utils libxkbcommon \
		wl-clipboard xclip xdotool python3-gobject gtk4 \
		dbus-daemon dbus-tools procps-ng libcap dotnet-sdk-10.0 libffi gcc SDL3-devel \
	&& dnf clean all

# kwin_wayland carries file capabilities (CAP_SYS_NICE), and a binary with them cannot exec in an
# unprivileged container.
RUN setcap -r "$(readlink -f /usr/bin/kwin_wayland)" \
	&& [ -z "$(getcap "$(readlink -f /usr/bin/kwin_wayland)")" ]

# a private dbus-daemon and the portals want a machine id, which the base image leaves empty
RUN dbus-uuidgen --ensure=/etc/machine-id

COPY hydratest-portal.py /usr/libexec/hydratest-portal
COPY hydratest.portal /usr/share/xdg-desktop-portal/portals/
COPY hydratest-portals.conf /usr/share/xdg-desktop-portal/
COPY desktop.sh input-probe.py lane.sh /usr/local/bin/
RUN chmod +x /usr/libexec/hydratest-portal /usr/local/bin/desktop.sh /usr/local/bin/input-probe.py /usr/local/bin/lane.sh

# a game: a fullscreen window holding the pointer locked in relative mode, printing the motion it sees
COPY game-probe.c /usr/local/src/
RUN gcc -std=c11 -O2 -Wall -Wextra -o /usr/local/bin/game-probe /usr/local/src/game-probe.c -lSDL3

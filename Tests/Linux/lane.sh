#!/usr/bin/env bash
# Container entrypoint for the linux lane, run as root in a --privileged container with the repository at /src.
# It sets up what the tests need and runs the whole suite as hydra-test under Xvfb, in a copy of the tree: the
# checkout keeps its owner, and only test-output comes back to it.
set -euo pipefail

user=hydra-test
home=/home/$user

# uinput test devices: their nodes appear in the kernel's devtmpfs, which a container's /dev is not
mkdir -p /run/devtmpfs /dev/input
mount -t devtmpfs devtmpfs /run/devtmpfs
mount --bind /run/devtmpfs/input /dev/input
# the container's own uinput node, not the host's
chmod 0666 /dev/uinput
# udev's part, which the container has no udev for: a test device's node (named hydra-test*), and only such a node,
# is made openable by anyone, then gets its record in /run/udev/data, which Hydra waits for before it opens a
# hotplugged node. The devtmpfs is the host's, so the host's udev handles the node too, and may reset its mode after
# this (a stock GROUP="input" makes it 0660 root:input): the test user is in the group the host gives input nodes
# (below), and the mode is set again on every pass. The host's own keyboards and mice are never opened: the tests
# read only their own devices, through links in a directory of their own. A uinput device lives under
# /sys/devices/virtual/input (the container's /sys/class/input lists none made after it started)
mkdir -p /run/udev/data
(
	while :; do
		for event in /sys/devices/virtual/input/input*/event*; do
			name="$(cat "${event%/*}/name" 2>/dev/null)" || continue
			[[ "$name" == hydra-test* ]] || continue
			number="$(cat "$event/dev" 2>/dev/null)" || continue
			node="/dev/input/${event##*/}"
			[ "$(stat -c %a "$node" 2>/dev/null)" = 666 ] || chmod 0666 "$node" 2>/dev/null || continue
			[ -e "/run/udev/data/c$number" ] || : > "/run/udev/data/c$number"
		done
		# a removed device's record goes with it, as udev's does, so a reused number waits again
		for record in /run/udev/data/c13:*; do
			[ -e "$record" ] || continue
			grep -qxF "${record#/run/udev/data/c}" /sys/devices/virtual/input/input*/event*/dev 2>/dev/null || rm -f "$record"
		done
		sleep 0.02
	done
) &

# the group the host's udev gives input nodes, as the host's own event nodes have it: the commonest group but root
input_gid="$(stat -c %g /dev/input/event* 2>/dev/null | grep -vx 0 | sort | uniq -c | sort -rn | awk 'NR == 1 { print $2 }')"

# an interface with a scoped IPv6 link-local address, which the relay's route lookup is tested against
sysctl -qw net.ipv6.conf.all.disable_ipv6=0 net.ipv6.conf.default.disable_ipv6=0
ip link add hydra-ll type dummy
ip link set hydra-ll up
for _ in $(seq 50); do ip -6 addr show dev hydra-ll scope link | grep -q inet6 && break; sleep 0.1; done

# the tree, without anything built from it, owned by the test user; package and crate caches in the cache volume
mkdir /work
tar -C /src --exclude=./test-output --exclude='*/bin' --exclude='*/obj' --exclude='*/node_modules' -cf - . | tar -C /work -xf -
mkdir -p /cache/nuget /cache/cargo /cache/hydra "$home/.local/share"
ln -s /cache/hydra "$home/.local/share/hydra"
chown -R "$user:$user" /work /cache "$home/.local"

rc=0
groups="$(id -G "$user" | tr ' ' ,)${input_gid:+,$input_gid}"
setpriv --reuid="$user" --regid="$user" --groups="$groups" --reset-env \
	env HOME="$home" PATH="$PATH" RUSTUP_HOME="$RUSTUP_HOME" CARGO_HOME=/cache/cargo NUGET_PACKAGES=/cache/nuget \
	DOTNET_CLI_TELEMETRY_OPTOUT=1 DOTNET_NOLOGO=1 \
	bash -c 'cd /work && xvfb-run -a -s "-screen 0 1280x1024x24 -noreset" dotnet test Hydra.sln "$@"' lane "$@" || rc=$?

if [ -d /work/test-output ]; then
	mkdir -p /src/test-output
	cp -r /work/test-output/. /src/test-output/
	chown -R --reference=/src /src/test-output
fi
exit "$rc"

#!/usr/bin/env bash
# Boots the qemu VM the hyprland lane runs in, and runs commands there in the Hyprland image.
#
#   vm.sh check          exits 0 when this host can run the VM, else names what is missing
#   vm.sh status         exits 0 when the VM is running
#   vm.sh up             boots it (once), provisions it and builds the image from Tests/Wayland
#   vm.sh run <cmd...>   up, then <cmd> in a privileged container of that image, on a fresh copy of the repo at /src
#   vm.sh down           powers it off
#
# Hyprland needs a KMS device and an active seat, which no container on a desktop host has: the VM gives it a
# virtio-gpu and a console nobody else holds. Everything it needs is cached in $HYDRA_VM_DIR (~/.cache/hydra-vm):
# the Fedora cloud image, the VM's own disk on top of it, its cloud-init seed and the ssh key that logs in as root.
set -euo pipefail

HERE="$(cd "$(dirname "$0")" && pwd)"
CONTEXT="$(dirname "$HERE")"
REPO="$(cd "$CONTEXT/../.." && pwd)"
VM_DIR="${HYDRA_VM_DIR:-$HOME/.cache/hydra-vm}"
PORT="${HYDRA_VM_SSH_PORT:-2222}"
IMAGE=hydra-hyprland
# the monitor Hyprland finds, which is the one size its desktops can have
XRES=1920 YRES=1080
BOOT_TIMEOUT=300
DOWN_TIMEOUT=60

die() { echo "vm.sh: $*" >&2; exit 1; }
usage() { die "usage: vm.sh check | status | up | run <cmd...> | down"; }

# The one place the host's architecture picks everything that differs: qemu, its acceleration and firmware,
# the Fedora image, and the Arch base (Arch Linux ARM's on arm64) with the keyring that verifies its packages.
FEDORA=https://download.fedoraproject.org/pub/fedora/linux/releases/45/Cloud
case "$(uname -s)/$(uname -m)" in
	Darwin/arm64)
		QEMU=qemu-system-aarch64
		QEMU_ARGS=(-machine "virt,highmem=on" -accel hvf -cpu host)
		FIRMWARE=edk2-aarch64-code.fd FIRMWARE_VARS=edk2-arm-vars.fd
		FEDORA_IMAGE="$FEDORA/aarch64/images/Fedora-Cloud-Base-Generic-45-1.4.aarch64.qcow2"
		ARCH_BASE=docker.io/menci/archlinuxarm:latest ARCH_KEYRING=archlinuxarm
		;;
	Linux/x86_64)
		QEMU=qemu-system-x86_64
		QEMU_ARGS=(-machine q35 -enable-kvm -cpu host -vga none)
		# Fedora's cloud image boots from BIOS too, so qemu's own SeaBIOS will do
		FIRMWARE='' FIRMWARE_VARS=''
		FEDORA_IMAGE="$FEDORA/x86_64/images/Fedora-Cloud-Base-Generic-45-1.4.x86_64.qcow2"
		ARCH_BASE=docker.io/library/archlinux:latest ARCH_KEYRING=archlinux
		;;
	*) QEMU='' ;;
esac
FEDORA_IMAGE="${HYDRA_VM_IMAGE_URL:-${FEDORA_IMAGE:-}}"

BASE_DISK="$VM_DIR/$(basename "${FEDORA_IMAGE:-none}")"
DISK="$VM_DIR/disk.qcow2"
SEED="$VM_DIR/seed.iso"
KEY="$VM_DIR/id_ed25519"
VARS="$VM_DIR/vars.fd"
PIDFILE="$VM_DIR/qemu.pid"
CONSOLE="$VM_DIR/console.log"

SSH=(ssh -i "$KEY" -p "$PORT" -o BatchMode=yes -o ConnectTimeout=5 -o StrictHostKeyChecking=no
	-o UserKnownHostsFile=/dev/null -o LogLevel=ERROR root@127.0.0.1)

# prints what this host lacks to run the VM, nothing when it lacks nothing
missing() {
	local tool
	[ -n "$QEMU" ] || { echo "a supported host (macOS arm64 or Linux x86_64), not $(uname -s)/$(uname -m)"; return; }
	for tool in "$QEMU" qemu-img ssh ssh-keygen curl tar; do
		command -v "$tool" > /dev/null || echo "$tool"
	done
	if [ "$(uname -s)" = Darwin ]; then
		command -v hdiutil > /dev/null || echo hdiutil
	elif ! command -v cloud-localds > /dev/null && ! command -v genisoimage > /dev/null; then
		echo "cloud-localds or genisoimage"
	fi
	if [ "$(uname -s)" = Linux ] && [ ! -w /dev/kvm ]; then echo "a writable /dev/kvm"; fi
	if [ -n "$FIRMWARE" ] && [ -z "$(firmware "$FIRMWARE")" ]; then echo "qemu's $FIRMWARE"; fi
}

# the path of one of qemu's firmware files, from the share directory beside its binary
firmware() {
	local share
	share="$(cd "$(dirname "$(command -v "$QEMU")")/../share/qemu" 2>/dev/null && pwd)" || return 0
	[ ! -f "$share/$1" ] || echo "$share/$1"
}

running() { [ -f "$PIDFILE" ] && kill -0 "$(cat "$PIDFILE")" 2>/dev/null; }

# the disk, seed and key, made once; the disk is an overlay, so the downloaded image stays pristine
create() {
	mkdir -p "$VM_DIR"
	if [ ! -f "$BASE_DISK" ]; then
		echo "vm.sh: downloading $FEDORA_IMAGE" >&2
		curl -fL --retry 3 -o "$BASE_DISK.part" "$FEDORA_IMAGE"
		mv "$BASE_DISK.part" "$BASE_DISK"
	fi
	[ -f "$KEY" ] || ssh-keygen -q -t ed25519 -N '' -C hydra-vm -f "$KEY"
	if [ ! -f "$SEED" ]; then
		local seed="$VM_DIR/seed"
		rm -rf "$seed"
		mkdir -p "$seed"
		printf 'instance-id: hydra-vm\nlocal-hostname: hydra-vm\n' > "$seed/meta-data"
		# Fedora's cloud config forbids root logins; with disable_root off, root gets the default user's keys as they are
		cat > "$seed/user-data" <<-EOF
			#cloud-config
			disable_root: false
			ssh_pwauth: false
			ssh_authorized_keys:
			  - $(cat "$KEY.pub")
		EOF
		if [ "$(uname -s)" = Darwin ]; then
			hdiutil makehybrid -quiet -iso -joliet -default-volume-name cidata -o "$SEED" "$seed"
		elif command -v cloud-localds > /dev/null; then
			cloud-localds "$SEED" "$seed/user-data" "$seed/meta-data"
		else
			genisoimage -quiet -output "$SEED" -volid cidata -joliet -rock "$seed/user-data" "$seed/meta-data"
		fi
	fi
	# room for the image's packages, the SDK and a build of the repo; cloud-init grows the root into it
	[ -f "$DISK" ] || qemu-img create -q -f qcow2 -b "$BASE_DISK" -F qcow2 "$DISK" 40G
	if [ -n "$FIRMWARE_VARS" ] && [ ! -f "$VARS" ]; then cp "$(firmware "$FIRMWARE_VARS")" "$VARS"; fi
}

boot() {
	# virtio-gpu 2D only: Mesa renders through GBM on llvmpipe, and Hyprland gets a KMS device it can be master of
	local args=("${QEMU_ARGS[@]}" -smp 6 -m 8192
		-drive "if=virtio,format=qcow2,file=$DISK"
		-drive "if=virtio,format=raw,readonly=on,file=$SEED"
		-netdev "user,id=net0,hostfwd=tcp:127.0.0.1:$PORT-:22" -device "virtio-net-pci,netdev=net0"
		-device virtio-rng-pci
		-device "virtio-gpu-pci,xres=$XRES,yres=$YRES"
		-display none -serial "file:$CONSOLE" -daemonize -pidfile "$PIDFILE")
	if [ -n "$FIRMWARE" ]; then
		args+=(-drive "if=pflash,format=raw,readonly=on,file=$(firmware "$FIRMWARE")" -drive "if=pflash,format=raw,file=$VARS")
	fi
	rm -f "$PIDFILE"
	"$QEMU" "${args[@]}"
}

# polls until ssh answers and cloud-init is done, failing if qemu dies or time runs out
wait_ssh() {
	local deadline=$((SECONDS + BOOT_TIMEOUT))
	until "${SSH[@]}" true 2>/dev/null; do
		if ! running; then
			tail -n 30 "$CONSOLE" >&2 || true
			die "qemu exited while the VM booted (console: $CONSOLE)"
		fi
		[ "$SECONDS" -lt "$deadline" ] || die "the VM's ssh did not answer on port $PORT after ${BOOT_TIMEOUT}s (console: $CONSOLE)"
		sleep 1
	done
	# cloud-init has finished growing the disk once this returns; it exits 2 for recoverable errors, which still boot
	"${SSH[@]}" 'cloud-init status --wait > /dev/null || [ $? -eq 2 ]'
}

# podman, tar and uinput, and no kmscon: Fedora's console takes DRM master, and Hyprland then cannot
provision() {
	"${SSH[@]}" bash -s <<-'EOF'
		set -euo pipefail
		missing=()
		for tool in podman tar; do command -v "$tool" > /dev/null || missing+=("$tool"); done
		[ "${#missing[@]}" -eq 0 ] || dnf install -y -q "${missing[@]}"
		units="$( { systemctl list-units --all --plain --no-legend --full 'kmscon*'; \
			systemctl list-unit-files --plain --no-legend --full 'kmscon*'; } | awk '{ print $1 }' | sort -u)"
		for unit in $units; do
			# a template cannot be stopped, only masked; its instances are listed and stopped on their own
			case "$unit" in *@.*) systemctl mask "$unit" > /dev/null ;; *) systemctl mask --now "$unit" > /dev/null ;; esac
		done
		deadline=$((SECONDS + 30))
		while pgrep kmscon > /dev/null; do
			[ "$SECONDS" -lt "$deadline" ] || { echo "kmscon is still running after it was masked and stopped" >&2; exit 1; }
			sleep 0.2
		done
		# the cloud image carries only the core modules, and uinput may be among the rest
		modprobe uinput 2>/dev/null || { dnf install -y -q "kernel-modules-$(uname -r)" && modprobe uinput; }
		[ -c /dev/uinput ] || { echo "the VM has no /dev/uinput" >&2; exit 1; }
		[ -c /dev/dri/card0 ] || { echo "the VM has no /dev/dri/card0" >&2; exit 1; }
	EOF
}

# the image, from Tests/Wayland as it is now; podman's layer cache makes an unchanged context cheap
build() {
	COPYFILE_DISABLE=1 tar --no-xattrs -C "$CONTEXT" --exclude=vm -cf - . \
		| "${SSH[@]}" 'rm -rf /root/hyprland-context && mkdir /root/hyprland-context && tar -xf - -C /root/hyprland-context'
	"${SSH[@]}" "podman build -q -t $IMAGE --build-arg BASE=$ARCH_BASE --build-arg KEYRING=$ARCH_KEYRING \
		-f /root/hyprland-context/Dockerfile.hyprland /root/hyprland-context > /dev/null"
}

up() {
	local why
	why="$(missing)"
	[ -z "$why" ] || die "this host cannot run the VM; missing: $(echo "$why" | paste -sd, - | sed 's/,/, /g')"
	if ! running; then
		create
		boot
	fi
	wait_ssh
	provision
	build
}

# The repo goes in as a fresh copy for every run, so a build in the VM never shares bin/obj with one on the host.
# Privileged with the VM's /dev, because Hyprland drives its DRM device and seat and the tests make uinput
# devices; the VM's network namespace and /run/udev, because libinput hears of a new device only through
# the udev monitor, whose netlink messages never leave the namespace udevd runs in.
run() {
	[ $# -gt 0 ] || usage
	up
	local cmd remote rc=0
	cmd="$(printf '%q ' "$@")"
	remote="set -eu
		d=\$(mktemp -d /var/tmp/hydra-src.XXXXXX)
		trap 'rm -rf \"\$d\"' EXIT
		tar -xf - -C \"\$d\"
		podman run --rm --privileged --network host -v /dev:/dev -v /run/udev:/run/udev:ro \
			-v \"\$d\":/src -w /src $IMAGE $cmd < /dev/null"
	COPYFILE_DISABLE=1 tar --no-xattrs -C "$REPO" --exclude=bin --exclude=obj --exclude=.opencode -cf - . \
		| "${SSH[@]}" "$remote" || rc=$?
	if [ "$rc" -eq 255 ]; then
		echo "vm.sh: the ssh link to the VM dropped (ssh exit 255), so this says nothing about the command." >&2
		echo "       Check it with 'vm.sh status' (console: $CONSOLE)." >&2
	fi
	return "$rc"
}

down() {
	running || { rm -f "$PIDFILE"; return 0; }
	local pid deadline=$((SECONDS + DOWN_TIMEOUT))
	pid="$(cat "$PIDFILE")"
	# the link drops as the VM goes down, so poweroff's own status means nothing
	"${SSH[@]}" 'systemctl poweroff' 2>/dev/null || true
	while kill -0 "$pid" 2>/dev/null; do
		if [ "$SECONDS" -ge "$deadline" ]; then
			echo "vm.sh: the VM did not power off in ${DOWN_TIMEOUT}s; killing qemu" >&2
			kill "$pid" 2>/dev/null || true
			break
		fi
		sleep 0.5
	done
	rm -f "$PIDFILE"
}

[ $# -ge 1 ] || usage
sub="$1"; shift
case "$sub" in
	check)
		why="$(missing)"
		[ -z "$why" ] || { echo "$why"; exit 1; }
		;;
	status) running ;;
	up) [ $# -eq 0 ] || usage; up ;;
	run) run "$@" ;;
	down) [ $# -eq 0 ] || usage; down ;;
	*) usage ;;
esac

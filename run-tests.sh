#!/usr/bin/env bash
# Hydra test runner — three lanes: native (this Mac), linux (headless X11 container), windows (real box).
#
#   - mac     → runs natively on this Mac. Windows-only tests self-skip; the [Category("Linux")] X11
#               tests self-skip too (no DISPLAY, and libX11 never loads), so a plain `dotnet test`
#               is safe. This is what runs on every commit.
#   - linux   → the real X11 selection-protocol tests (XorgClipboardSync) against a headless Xvfb
#               server, inside a Linux dotnet-sdk container with Xvfb + xclip.
#   - windows → the Windows-only tests (WinKeyResolver ToUnicodeEx, ProcessLock file locking) and the
#               rest of the suite, on the real Windows box. The working tree is synced over SSH to
#               $WIN_DIR and built/tested there (the box keeps its own bin/obj between runs, so builds
#               are incremental). Requires an SSH host alias "windows" with the .NET SDK installed.
#
# Usage:
#   ./run-tests.sh          # default: mac, then linux
#   ./run-tests.sh mac      # only the Mac-native run
#   ./run-tests.sh linux    # only the container X11 lane (Category=Linux, under Xvfb)
#   ./run-tests.sh windows  # only the Windows lane (sync + test on the real box)
#   ./run-tests.sh all      # mac + linux + windows
set -euo pipefail

cd "$(dirname "$0")"

SDK_IMAGE="mcr.microsoft.com/dotnet/sdk:10.0"
SLN="Hydra.sln"
WIN_HOST="windows"
WIN_DIR='C:\tmp\hydra'

run_mac() {
	echo "── Mac-native tests (Windows/X11 tests self-skip) ─────────────────"
	dotnet test "$SLN"
}

# Container X11 lane: install Xvfb + xclip + the X client libs, then run the Category=Linux
# tests under xvfb-run (which starts a headless X server, sets DISPLAY, and tears it down).
run_linux() {
	echo "── Container X11 tests (Category=Linux, headless Xvfb) ────────────"
	docker run --rm -v "$PWD":/src -w /src "$SDK_IMAGE" bash -c '
		set -e
		apt-get update -qq
		DEBIAN_FRONTEND=noninteractive apt-get install -y -qq \
			xvfb xclip procps libx11-6 libxi6 libxfixes3 >/dev/null
		xvfb-run -a dotnet test Hydra.sln --filter "Category=Linux" -- NUnit.NumberOfTestWorkers=1
	'
}

# Windows lane: sync the working tree (sans build artifacts) to the Windows box and test there.
# COPYFILE_DISABLE stops macOS tar from emitting ._ AppleDouble sidecars; bin/obj are excluded so
# the box builds its own (and keeps them between runs for incremental builds). The default cmd.exe
# SSH shell needs `cd /d` and `md`.
run_windows() {
	echo "── Windows tests (synced to $WIN_HOST:$WIN_DIR) ───────────────────"
	ssh "$WIN_HOST" "if not exist $WIN_DIR md $WIN_DIR"
	# .git is excluded for speed, but Tests/Setup/TestLog.FindSolutionRoot needs a .sln + a .git dir
	# to locate the test-output folder — so drop an empty .git marker after extracting.
	COPYFILE_DISABLE=1 tar czf - \
		--exclude='./.git' --exclude='*/bin' --exclude='*/obj' --exclude='./test-output' \
		-C "$PWD" . | ssh "$WIN_HOST" "cd /d $WIN_DIR && tar xzf - && if not exist .git md .git"
	ssh "$WIN_HOST" "cd /d $WIN_DIR && dotnet test Hydra.sln"
}

case "${1:-default}" in
	mac) run_mac ;;
	linux) run_linux ;;
	windows) run_windows ;;
	default) run_mac; run_linux ;;
	all) run_mac; run_linux; run_windows ;;
	*) echo "usage: $0 [mac|linux|windows|default|all]" >&2; exit 2 ;;
esac

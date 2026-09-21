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
#               are incremental).
#
#               Needs an SSH host whose login shell is cygwin's bash, with the .NET SDK installed, named
#               by HYDRA_WINDOWS_TEST_HOST — TEST_ in the name because this box is only ever somewhere to
#               RUN THE SUITE, and nothing about running Hydra itself reads it. There is deliberately no
#               default: a hostname baked in here would be one maintainer's machine, and everyone else
#               would get an ssh error for a lane they never asked to have.
#
#               UNSET is a clean skip, because you never claimed to have the box — `./run-tests.sh all`
#               still succeeds, saying plainly which tests it did not cover. SET BUT UNREACHABLE is a
#               FAILURE, because you did claim it. Same contract as the Docker and kernel-mount gates in
#               the sibling repo: a lane that declares it can provide something must fail when it
#               cannot, and a lane that never claimed it may skip.
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
WIN_HOST="${HYDRA_WINDOWS_TEST_HOST:-}"
WIN_DIR="${HYDRA_WINDOWS_TEST_PATH:-/cygdrive/c/tmp/hydra}"
# Kept OUTSIDE the synced tree so a re-sync never wipes the package cache or the fabricated profile.
WIN_NUGET="${HYDRA_WINDOWS_TEST_NUGET_PACKAGES:-C:\\tmp\\nuget-packages}"
WIN_PROFILE="${HYDRA_WINDOWS_TEST_PROFILE:-C:\\tmp\\lane-profile}"

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
# the box builds its own (and keeps them between runs for incremental builds).
#
# The remote session's preamble is not decoration. Every line of it was paid for on erebus's lane
# against this same box, and the two should not drift:
#
#   PATH        an ssh session gets a PATH a login shell would have extended, so dotnet is not on it
#   PROFILEREAD cygwin's /etc/profile skips its PATH setup when this is set, which loads a SECOND
#               cygwin1.dll into the process — fatal, and silent. `unset` alone is not enough: as
#               SYSTEM the variable is readonly, so `env -u` strips it from the child that matters
#   NuGet/APPDATA  as SYSTEM these are all empty, NuGet composes a null path, and every restore dies
#               with "Value cannot be null. (Parameter 'path1')" and nothing naming the cause
#   MSBuild     worker nodes stay alive and hold output DLLs open, so the next run fails MSB3027
#   exit code   /etc/bash.bash_logout runs `clear`, which fails without TERM — under `set -e` that
#               failure becomes the session's exit status and a fully green run reports as failed
run_windows() {
	if [ -z "$WIN_HOST" ]; then
		echo "── Windows tests: skipped (no HYDRA_WINDOWS_TEST_HOST) ───────────"
		echo "   WinKeyResolver's ToUnicodeEx tests and ProcessLock's file locking need a real Windows" >&2
		echo "   box; they self-skip in the mac lane, so this run does not cover them. Set" >&2
		echo "   HYDRA_WINDOWS_TEST_HOST to an ssh host running cygwin sshd with the .NET SDK to include them." >&2
		return 0
	fi

	if ! ssh -o BatchMode=yes -o ConnectTimeout=10 "$WIN_HOST" true 2>/dev/null; then
		echo "── Windows tests: FAILED ──────────────────────────────────────────"
		echo "   HYDRA_WINDOWS_TEST_HOST names '$WIN_HOST', which is not reachable over ssh." >&2
		echo "   Naming a box is a claim that it is there, so this is a failure and not a skip." >&2
		return 1
	fi

	echo "── Windows tests (synced to $WIN_HOST:$WIN_DIR) ───────────────────"
	ssh "$WIN_HOST" "mkdir -p '$WIN_DIR'"
	# .git is excluded for speed, but Tests/Setup/TestLog.FindSolutionRoot needs a .sln + a .git dir
	# to locate the test-output folder — so drop an empty .git marker after extracting.
	COPYFILE_DISABLE=1 tar czf - \
		--exclude='./.git' --exclude='*/bin' --exclude='*/obj' --exclude='./test-output' \
		-C "$PWD" . | ssh "$WIN_HOST" "cd '$WIN_DIR' && tar xzf - && mkdir -p .git"

	ssh "$WIN_HOST" bash <<REMOTE
export PATH="\$PATH:/cygdrive/c/Program Files/dotnet"
export NUGET_PACKAGES='$WIN_NUGET'
export USERPROFILE='$WIN_PROFILE'
export APPDATA='$WIN_PROFILE\\AppData\\Roaming'
export LOCALAPPDATA='$WIN_PROFILE\\AppData\\Local'
mkdir -p "\$(cygpath -u '$WIN_NUGET')" "\$(cygpath -u '$WIN_PROFILE')/AppData/Roaming" "\$(cygpath -u '$WIN_PROFILE')/AppData/Local"
unset PROFILEREAD ORIGINAL_PATH 2>/dev/null || true
export MSBUILDDISABLENODEREUSE=1
export MSBUILDTERMINALLOGGER=off
export TERM="\${TERM:-dumb}"
set -euo pipefail
cd '$WIN_DIR'
echo "==> building"
dotnet build Hydra.sln -v q --nologo
echo "==> running tests"
set +e
env -u PROFILEREAD -u ORIGINAL_PATH dotnet test Hydra.sln --no-build --nologo
rc=\$?
exit \$rc
REMOTE
}

case "${1:-default}" in
	mac) run_mac ;;
	linux) run_linux ;;
	windows) run_windows ;;
	default) run_mac; run_linux ;;
	all) run_mac; run_linux; run_windows ;;
	*) echo "usage: $0 [mac|linux|windows|default|all]" >&2; exit 2 ;;
esac

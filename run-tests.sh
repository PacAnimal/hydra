#!/usr/bin/env bash
# Hydra test runner.
#
# Tests split by NUnit category:
#   - [Category("Linux")] → real X11 selection-protocol tests (XorgClipboardSync). Need a live
#                           X server; run headless inside a Linux dotnet-sdk container with Xvfb
#                           and xclip. They self-skip off Linux (no DISPLAY), and libX11 never
#                           loads there, so a plain `dotnet test` on macOS/Windows is safe.
#   - everything else     → runs natively on this Mac. (Windows-only tests are excluded from the
#                           build on non-Windows via Tests.csproj.)
#
# Usage:
#   ./run-tests.sh          # default: Mac native (X11 tests skip), then the container X11 lane
#   ./run-tests.sh mac      # only the Mac-native run (X11 tests skip)
#   ./run-tests.sh linux    # only the container X11 lane (Category=Linux, under Xvfb)
set -euo pipefail

cd "$(dirname "$0")"

SDK_IMAGE="mcr.microsoft.com/dotnet/sdk:10.0"
SLN="Hydra.sln"

run_mac() {
	echo "── Mac-native tests (X11 tests self-skip) ─────────────────────────"
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

case "${1:-default}" in
	mac) run_mac ;;
	linux) run_linux ;;
	default) run_mac; run_linux ;;
	*) echo "usage: $0 [mac|linux|default]" >&2; exit 2 ;;
esac

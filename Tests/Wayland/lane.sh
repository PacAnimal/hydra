#!/usr/bin/env bash
# Container entrypoint for the desktops lane. Tests start their own desktops through desktop.sh.
set -euo pipefail

mkdir -p /tmp/hd
exec "$@"

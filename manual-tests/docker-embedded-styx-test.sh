#!/usr/bin/env bash
# docker-embedded-styx-test.sh
#
# Builds Hydra from source inside Docker, spins up master + slave containers
# on a shared network, validates embedded Styx relay connection and
# authentication, then tears everything down.
#
# Safe to re-run after an interrupted attempt — pre-cleanup removes leftovers.

set -euo pipefail

SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
NETWORK="hydra-test-net"
MASTER_NAME="hydra-test-master"
SLAVE_NAME="hydra-test-slave"
IMAGE="hydra-test:latest"
TIMEOUT=120

case "$(uname -m)" in
    x86_64)          RID="linux-x64" ;;
    arm64|aarch64)   RID="linux-arm64" ;;
    *) echo "unsupported architecture: $(uname -m)"; exit 1 ;;
esac
BINARY="/src/Hydra/bin/Release/net10.0/$RID/publish/Hydra"
CONF_DIR="$(mktemp -d)"

# ── helpers ────────────────────────────────────────────────────────────────────

pre_cleanup() {
    echo "=== pre-cleanup: removing any stale containers / network ==="
    docker rm -f "$MASTER_NAME" "$SLAVE_NAME" 2>/dev/null || true
    docker network rm "$NETWORK" 2>/dev/null || true
}

cleanup() {
    echo ""
    echo "=== teardown ==="
    docker rm -f "$MASTER_NAME" "$SLAVE_NAME" 2>/dev/null || true
    docker network rm "$NETWORK" 2>/dev/null || true
    rm -f "$SCRIPT_DIR/.dockerignore"
    rm -rf "$CONF_DIR"
}
trap cleanup EXIT

wait_for_log() {
    local container="$1" pattern="$2"
    local end=$(( $(date +%s) + TIMEOUT ))
    printf "  waiting for '%s' ..." "$pattern"
    while [ "$(date +%s)" -lt "$end" ]; do
        if docker logs "$container" 2>&1 | grep -q "$pattern"; then
            echo " ok"
            return 0
        fi
        if [ "$(docker inspect -f '{{.State.Running}}' "$container" 2>/dev/null)" = "false" ]; then
            echo " CONTAINER EXITED"
            break
        fi
        sleep 2
    done
    echo ""
    echo "FAILED: '$pattern' not seen in $container within ${TIMEOUT}s"
    echo "--- $container logs (last 60 lines) ---"
    docker logs "$container" 2>&1 | tail -60
    return 1
}

# ── pre-run cleanup ────────────────────────────────────────────────────────────

pre_cleanup

# ── build docker image ─────────────────────────────────────────────────────────

echo ""
echo "=== building Docker image (Hydra compiled from source) ==="

cat > "$SCRIPT_DIR/.dockerignore" <<'IGNORE'
**/bin/
**/obj/
.git/
HydraWebConfig/node_modules/
docs/assets/
IGNORE

docker build -t "$IMAGE" --build-arg RID="$RID" -f - "$SCRIPT_DIR" <<'DOCKERFILE'
FROM mcr.microsoft.com/dotnet/sdk:10.0
ARG RID
RUN apt-get update && apt-get install -y --no-install-recommends \
    libx11-6 libxi6 libxtst6 libxext6 libxrandr2 \
    libxkbcommon0 libxss1 libxfixes3 xvfb \
    && rm -rf /var/lib/apt/lists/*
WORKDIR /src
COPY . .
RUN dotnet publish Hydra --runtime $RID --self-contained -c Release
DOCKERFILE

rm -f "$SCRIPT_DIR/.dockerignore"

# ── configs ────────────────────────────────────────────────────────────────────

cat > "$CONF_DIR/master.conf" <<'EOF'
{
  "name": "master",
  "profiles": [{
    "mode": "Master",
    "embeddedStyxServer": { "port": 5000, "password": "test-secret" },
    "hosts": [
      { "name": "master", "neighbours": [{ "direction": "right", "name": "slave" }] },
      { "name": "slave" }
    ]
  }]
}
EOF

cat > "$CONF_DIR/slave.conf" <<'EOF'
{
  "name": "slave",
  "profiles": [{
    "mode": "Slave",
    "embeddedStyx": { "server": "http://hydra-test-master:5000", "password": "test-secret" }
  }]
}
EOF

# ── network ────────────────────────────────────────────────────────────────────

docker network create "$NETWORK"

# ── master ─────────────────────────────────────────────────────────────────────

echo ""
echo "=== starting master ==="
docker run -d \
    --name "$MASTER_NAME" \
    --network "$NETWORK" \
    -v "$CONF_DIR/master.conf:/tmp/hydra.conf:ro" \
    "$IMAGE" \
    sh -c "Xvfb :1 -screen 0 1024x768x24 -nolisten tcp >/dev/null 2>&1 & sleep 1 && DISPLAY=:1 CONFIG=/tmp/hydra.conf $BINARY"

wait_for_log "$MASTER_NAME" "Embedded Styx relay listening on port 5000"

# ── slave ──────────────────────────────────────────────────────────────────────

echo ""
echo "=== starting slave ==="
docker run -d \
    --name "$SLAVE_NAME" \
    --network "$NETWORK" \
    -v "$CONF_DIR/slave.conf:/tmp/hydra.conf:ro" \
    "$IMAGE" \
    sh -c "Xvfb :1 -screen 0 1024x768x24 -nolisten tcp >/dev/null 2>&1 & sleep 1 && DISPLAY=:1 CONFIG=/tmp/hydra.conf $BINARY"

wait_for_log "$SLAVE_NAME" "Connected to Styx relay"
wait_for_log "$SLAVE_NAME" "Authenticated on relay as slave"
printf "  waiting for master to see slave as peer ..."
_end=$(( $(date +%s) + TIMEOUT ))
while [ "$(date +%s)" -lt "$_end" ]; do
    if docker logs "$MASTER_NAME" 2>&1 | grep "Peers online:" | grep -qv "(none)"; then
        echo " ok"
        break
    fi
    if [ "$(docker inspect -f '{{.State.Running}}' "$MASTER_NAME" 2>/dev/null)" = "false" ]; then
        echo " CONTAINER EXITED"
        echo "FAILED: master exited before seeing peer"
        docker logs "$MASTER_NAME" 2>&1 | tail -30
        exit 1
    fi
    sleep 2
done
if [ "$(date +%s)" -ge "$_end" ]; then
    echo ""
    echo "FAILED: master never saw a live peer within ${TIMEOUT}s"
    docker logs "$MASTER_NAME" 2>&1 | tail -30
    exit 1
fi

# ── result ─────────────────────────────────────────────────────────────────────

echo ""
echo "=== ALL CHECKS PASSED ==="
echo "  [master] embedded Styx relay started on port 5000"
echo "  [slave]  connected to relay"
echo "  [slave]  authenticated on relay"
echo "  [master] acknowledges slave as active peer"

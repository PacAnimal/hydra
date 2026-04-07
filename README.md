# Hydra

A software KVM — share one keyboard and mouse across multiple computers by moving the cursor to the edge of the screen.

Hydra runs on the machine with the physical keyboard and mouse (the **master**). When you move the cursor past the edge of the screen, input is transparently forwarded to the target machine (the **slave**) over the network.

## Features

- Seamless cursor transitions across screen edges in any direction (left, right, up, down)
- **Multi-monitor support** — multiple local and remote monitors, auto-detected from the OS at startup and when screens are connected/disconnected
- Flexible layout: configure arbitrary topologies — L-shaped, grids, or any combination
- **Range-based neighbours** — split edges to route to different hosts depending on cursor position (Synergy-style)
- **Per-screen scale** — control cursor speed on each remote screen via `screenDefinitions`
- Full keyboard forwarding, including dead keys and special characters — resolved on the master using its own keyboard layout
- Mouse button and scroll forwarding
- End-to-end encrypted relay via **Styx** for machines on different networks
- macOS, Windows, and Linux support

## Requirements

- .NET 10
- **macOS**: Accessibility permission (System Settings → Privacy & Security → Accessibility)
- **Linux**: X11 with XInput2 (Wayland not yet supported)

## Configuration

Edit `hydra.conf` (sits next to the binary):

```json
{
  "mode": "Master",
  "name": "laptop",
  "logLevel": "info",
  "hosts": [
    {
      "name": "laptop",
      "neighbours": [
        { "direction": "right", "name": "desktop" }
      ]
    }
  ]
}
```

Neighbours are **mirrored by default** — declaring that `laptop` has `desktop` to the right automatically creates the reverse: `desktop` has `laptop` to its left. You only need to declare one side. Both sides can still be declared explicitly if needed (the mirror is skipped if the reverse already exists).

### Config fields

- `mode` — `Master` or `Slave`
- `name` — this machine's name on the network. Optional — defaults to the machine's hostname without domain. Must match one of the host names for the master to identify its own screen.
- `logLevel` — `trce`, `dbug`, `info`, `warn`, `fail`, or `crit`
- `networkConfig` — base64 relay config string from Styx (required when using the relay)
- `hosts` — list of host entries for the neighbour graph (master only; slaves don't need this)
- `screenDefinitions` — optional per-screen configuration for scale (used on any machine)
- `deadCorners` — pixel dead zone at screen corners where transitions are blocked (default `0`, `50` is a reasonable starting value). Scaled by the screen's `scale` setting. Can also be set per-host to override.
- `conditions` — optional object; if set, this config only activates when all conditions are met (see [Network-aware config](#network-aware-config))
  - `ssid` — activates when connected to this WiFi network name (case-insensitive)

### Screen layout

Each entry in `hosts` represents one machine. Declare your neighbours by direction:

```json
{
  "name": "laptop",
  "neighbours": [
    { "direction": "right", "name": "desktop" },
    { "direction": "up",    "name": "tv-box"  }
  ]
}
```

Supported directions: `left`, `right`, `up`, `down`.

**Neighbour options**:

| Field | Default | Description |
|-------|---------|-------------|
| `direction` | required | Which edge of this host triggers the transition |
| `name` | required | Target host name |
| `mirror` | `true` | Auto-create the reverse mapping on the target host (skipped if the reverse already exists) |
| `sourceStart` | `0` | Start of the source edge range (0–100%), inclusive |
| `sourceEnd` | `100` | End of the source edge range (0–100%), inclusive |
| `destStart` | `0` | Start of the destination edge range (0–100%) |
| `destEnd` | `100` | End of the destination edge range (0–100%) |
| `sourceScreen` | `null` | Restrict to a specific local screen — match by `screenName`, `displayName`, `output`, or `platformId` |
| `destScreen` | `null` | Target a specific screen on the remote host — same identifiers as `sourceScreen` |

**Range-based neighbours** let you split an edge to route to different hosts depending on where the cursor crosses:

```json
{
  "name": "laptop",
  "neighbours": [
    { "direction": "right", "name": "workstation", "sourceStart": 0,  "sourceEnd": 50  },
    { "direction": "right", "name": "monitor-host", "sourceStart": 50, "sourceEnd": 100 }
  ]
}
```

When cursor crosses the right edge in the top half (0–50%), it goes to `workstation`; bottom half goes to `monitor-host`.

### Dead corners

`deadCorners` defines a pixel dead zone at each corner of the screen where outbound transitions are blocked, regardless of neighbour config. The value is in pixels — `50` means the cursor must be more than 50 pixels away from a corner to trigger a transition. The pixel value is multiplied by the screen's `mouseScale` setting, so a high-DPI screen with `mouseScale: 2` and `deadCorners: 50` gets an effective 100-pixel dead zone.

Set at the root level to apply to all hosts:

```json
{
  "mode": "Master",
  "deadCorners": 50,
  "hosts": [...]
}
```

Override per-host (takes precedence over the root value):

```json
{
  "hosts": [
    {
      "name": "laptop",
      "deadCorners": 80,
      "neighbours": [...]
    }
  ]
}
```

Transitions into a host through a corner are unaffected — dead corners only block outbound transitions.

**Missing hosts**: if a peer is offline, Hydra skips through to the next machine in the same direction (if configured). This lets you maintain a logical layout even when a machine in the middle of the chain is down.

### Multi-monitor

Local screens are **auto-detected from the OS** — no config is required. On startup, Hydra logs all detected screens with their identifiers:

```
Local screens: 2
  Screen 0: {"screenName":"laptop:0","displayName":"Built-in Retina Display","platformId":"1"}
  Screen 1: {"screenName":"laptop:1","displayName":"DELL U2720Q","output":"HDMI-1","platformId":"2"}
```

Use these identifiers in `sourceScreen`/`destScreen` to target specific monitors in neighbour rules, and in `screenDefinitions` to set per-screen options. Only non-null properties are shown — `output` is omitted on platforms that don't expose connector names.

### Screen definitions

`screenDefinitions` is available on both master and slave. Each entry specifies one or more match criteria — all specified criteria must match (case-insensitive). Use the identifiers shown at startup to build match entries.

```json
{
  "mouseScale": 1.5,
  "screenDefinitions": [
    { "displayName": "DELL U2720Q",             "mouseScale": 1.5 },
    { "displayName": "Built-in Retina Display", "mouseScale": 1.0 },
    { "outputName": "HDMI-1",                   "mouseScale": 0.8 }
  ]
}
```

| Field | Default | Description |
|-------|---------|-------------|
| `displayName` | — | Match by display/monitor name (e.g. `"DELL U2720Q"`) |
| `outputName` | — | Match by output connector name (e.g. `"HDMI-1"`) |
| `platformId` | — | Match by platform-specific ID |
| `mouseScale` | — | Cursor speed multiplier on this screen; overrides root `mouseScale` |

The root-level `mouseScale` sets a fallback multiplier for all screens on that machine. Per-screen `mouseScale` in a `screenDefinitions` entry overrides it. If neither is set, the multiplier defaults to `1.0`.

At least one match field must be set per `screenDefinitions` entry.

### Network-aware config

If you move your machine between networks (e.g. home vs. office), `hydra.conf` can be an **array** of configs, each gated on the current network. Hydra picks the matching config and restarts automatically when the network changes. If no config matches — for example, you're at a coffee shop — Hydra idles silently. This is intentional: there are no machines to connect to anyway.

```json
[
  {
    "mode": "Master",
    "conditions": { "ssid": "HomeWifi" },
    "name": "laptop",
    "hosts": [
      { "name": "laptop", "neighbours": [{ "direction": "right", "name": "desktop" }] }
    ]
  },
  {
    "mode": "Slave",
    "conditions": { "ssid": "OfficeWifi" },
    "name": "laptop",
    "networkConfig": "<base64 string>"
  }
]
```

- `conditions: { "ssid": "..." }` — activates when connected to the named WiFi network (case-insensitive)
- No `conditions` — fallback, activates on any network not matched by another entry

A fallback entry (no `conditions`) is optional. Without one, Hydra simply idles on unrecognised networks.

Rules: at most one fallback, no duplicate SSIDs — validated at startup.

> **macOS note:** Location Services permission is only requested if at least one config uses `conditions`. Hydra never asks for location permission when running with a single unconditional config.

### Lock hotkey

**Ctrl+Alt+Super+L** — locks the cursor to the current screen until pressed again.

## Running

```bash
dotnet run --project Hydra
```

Or build a self-contained single-file executable:

```bash
dotnet publish Hydra --runtime osx-arm64 --self-contained   # macOS Apple Silicon
dotnet publish Hydra --runtime win-x64  --self-contained    # Windows x64
dotnet publish Hydra --runtime linux-x64 --self-contained   # Linux x64
```

Output lands in `Hydra/bin/Release/net10.0/<rid>/publish/`. The binary bundles the runtime and all assets — nothing else needs to ship alongside it.

## Slave setup

On the slave machine:

```json
{
  "mode": "Slave",
  "name": "desktop",
  "logLevel": "info",
  "networkConfig": "<same base64 string as master>",
  "screenDefinitions": [
    { "displayName": "DELL U2720Q", "mouseScale": 1.5 }
  ]
}
```

Slaves do not need a `hosts` section — they receive and replay input from the master. The slave auto-detects all local monitors and reports them to the master so the master knows the full remote screen layout.

## Networking with Styx

For machines on different networks, **Styx** is a relay server that securely tunnels Hydra connections.

### Running Styx

```bash
docker run -e RELAY_PASSWORD=<secret> -p 5000:5000 ghcr.io/pacanimal/styx:latest
```

Styx listens on port `5000` by default. Override with `LOCAL_PORT`:

```bash
docker run -e RELAY_PASSWORD=<secret> -e LOCAL_PORT=8080 -p 8080:8080 ghcr.io/pacanimal/styx:latest
```

Or build from source:

```bash
docker build -f Styx/Dockerfile -t styx:local .
docker run -e RELAY_PASSWORD=<secret> -p 5000:5000 styx:local
```

### Generating a network config

Open `http://<your-styx-host>:5000` in a browser, enter the relay password, and click **Generate**. Copy the config string.

### Connecting Hydra to Styx

Add `networkConfig` to `hydra.conf` on both machines. Use the same config string on all machines in a network.

**Master** (`hydra.conf`):

```json
{
  "mode": "Master",
  "name": "laptop",
  "networkConfig": "<base64 string from the Styx web UI>",
  "logLevel": "info",
  "hosts": [
    {
      "name": "laptop",
      "neighbours": [{ "direction": "right", "name": "desktop" }]
    }
  ]
}
```

**Slave** (`hydra.conf`):

```json
{
  "mode": "Slave",
  "name": "desktop",
  "networkConfig": "<same base64 string>",
  "logLevel": "info"
}
```

- Both machines must use the **same** network config string.
- Traffic between Hydra instances is end-to-end encrypted — Styx only routes opaque bytes.

## Building from source

```bash
dotnet build Hydra.sln
dotnet test Hydra.sln
```

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
    },
    {
      "name": "desktop",
      "neighbours": [
        { "direction": "left", "name": "laptop" }
      ]
    }
  ]
}
```

### Config fields

- `mode` — `Master` or `Slave`
- `name` — this machine's name on the network. Optional — defaults to the machine's hostname without domain. Must match one of the host names for the master to identify its own screen.
- `logLevel` — `trce`, `dbug`, `info`, `warn`, `fail`, or `crit`
- `networkConfig` — base64 relay config string from Styx (required when using the relay)
- `hosts` — list of host entries for the neighbour graph (master only; slaves don't need this)
- `screenDefinitions` — optional per-screen configuration for scale (used on any machine)
- `condition` — optional network condition: `Wired` or `Ssid` (see [Network-aware config](#network-aware-config))
- `ssid` — the WiFi network name to match; required when `condition` is `Ssid`

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
| `sourceStart` | `0` | Start of the source edge range (0–100%), inclusive |
| `sourceEnd` | `100` | End of the source edge range (0–100%), inclusive |
| `destStart` | `0` | Start of the destination edge range (0–100%) |
| `destEnd` | `100` | End of the destination edge range (0–100%) |
| `sourceScreen` | `null` | Restrict to a specific local screen (by display name or output name) |
| `destScreen` | `null` | Target a specific screen on the remote host |

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

**Missing hosts**: if a peer is offline, Hydra skips through to the next machine in the same direction (if configured). This lets you maintain a logical layout even when a machine in the middle of the chain is down.

### Multi-monitor

Local screens are **auto-detected from the OS** — no config is required. On startup, Hydra logs all detected screens with their identifiers:

```
Local screens: 2
  Screen 0: 2560x1600 @ (0,0)  output=eDP-1  name=Built-in Retina Display
  Screen 1: 3840x2160 @ (2560,0)  output=HDMI-1  name=DELL U2720Q
```

Use these identifiers in `screenDefinitions` to set per-screen options, and in `sourceScreen`/`destScreen` to target specific monitors in neighbour rules.

### Screen definitions

`screenDefinitions` is available on both master and slave. The `match` field is compared against the display name, output connector name, or platform ID of each detected screen (case-insensitive).

```json
{
  "screenDefinitions": [
    { "match": "DELL U2720Q",              "scale": 1.5 },
    { "match": "Built-in Retina Display",  "scale": 1.0 },
    { "match": "HDMI-1",                   "scale": 0.8 }
  ]
}
```

| Field | Default | Description |
|-------|---------|-------------|
| `match` | required | Screen identifier (display name, output name, or platform ID) |
| `scale` | `1.0` | Cursor speed multiplier on this screen |

Screens with no matching definition use default scale (1.0).

### Network-aware config

If you move your machine between networks (e.g. home vs. office), `hydra.conf` can be an **array** of configs, each gated on the current network. Hydra picks the first matching config and restarts automatically when the network changes.

```json
[
  {
    "mode": "Master",
    "condition": "Ssid",
    "ssid": "OfficeWifi",
    "name": "laptop",
    "networkConfig": "<base64 string>",
    "hosts": [
      { "name": "laptop", "neighbours": [{ "direction": "right", "name": "desktop" }] },
      { "name": "desktop", "neighbours": [{ "direction": "left", "name": "laptop" }] }
    ]
  },
  {
    "mode": "Slave",
    "name": "laptop"
  }
]
```

- `condition: "Ssid"` — activates when connected to the named WiFi network (`ssid` field required)
- `condition: "Wired"` — activates when connected via Ethernet
- No `condition` — fallback, matches any network not covered by other entries

A config without a `condition` is the default fallback. If no config matches the current network (and there is no fallback), Hydra idles silently until the network changes.

Rules: at most one default, no duplicate SSIDs, no duplicate `Wired` entries — validated at startup.

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
    { "match": "DELL U2720Q", "scale": 1.5 }
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
    },
    {
      "name": "desktop",
      "neighbours": [{ "direction": "left", "name": "laptop" }]
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

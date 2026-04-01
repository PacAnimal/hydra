# Hydra

A software KVM — share one keyboard and mouse across multiple computers by moving the cursor to the edge of the screen.

Hydra runs on the machine with the physical keyboard and mouse (the **master**). When you move the cursor past the edge of the screen, input is transparently forwarded to the target machine (the **slave**) over the network.

## Features

- Seamless cursor transitions across screen edges in any direction (left, right, up, down)
- Flexible layout: configure arbitrary topologies — L-shaped, grids, or any combination
- Scale factor per neighbour — control how fast the cursor moves on each remote screen
- Offset per neighbour — shift the entry point when crossing a screen edge
- Full keyboard forwarding, including dead keys and special characters — resolved on the master using its own keyboard layout, so slaves always get the right character regardless of their layout
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
  "screens": [
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
- `name` — this machine's name on the network. Optional — defaults to the machine's hostname without domain. Must match one of the screen names for the master to identify its own screen.
- `logLevel` — `trce`, `dbug`, `info`, `warn`, `fail`, or `crit`
- `networkConfig` — base64 relay config string from Styx (required when using the relay)

### Screen layout

Each entry in `screens` represents one machine. Declare your neighbours by direction:

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

**Neighbour options** (all optional):

| Field | Default | Description |
|-------|---------|-------------|
| `scale` | `1.0` | Mouse speed multiplier on the destination screen. `0.5` = half speed, `2.0` = double speed. |
| `offset` | `0` | Shifts the entry point when crossing an edge, as a percentage of the destination screen's perpendicular dimension. Positive = down/right, negative = up/left. Range: -99 to 99. |

Example — a desktop that is taller than the laptop, mounted slightly lower:

```json
{ "direction": "right", "name": "desktop", "scale": 0.8, "offset": 15 }
```

**Missing screens**: if a peer is offline, Hydra skips through to the next machine in the same direction (if configured). This lets you maintain a logical layout even when a machine in the middle of the chain is down.

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
  "networkConfig": "<same base64 string as master>"
}
```

Slaves do not need a `screens` section — they receive and replay input from the master.

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
  "screens": [
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

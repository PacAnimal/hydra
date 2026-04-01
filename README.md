# Hydra

A software KVM — share one keyboard and mouse across multiple computers by moving the cursor to the edge of the screen.

Hydra runs on the machine with the physical keyboard and mouse (the **master**). When you move the cursor past the edge of the screen, input is transparently forwarded to the target machine (the **slave**) over the network.

## Features

- Seamless cursor transitions across screen edges
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
  "logLevel": "info",
  "screens": [
    { "name": "main",  "x": 0, "y": 0, "width": 2560, "height": 1440, "isVirtual": false },
    { "name": "right", "x": 0, "y": 0, "width": 1920, "height": 1080, "isVirtual": true  }
  ]
}
```

- `logLevel` — `trce`, `dbug`, `info`, `warn`, `fail`, or `crit`
- `screens` — list your physical screen first (`isVirtual: false`), then one virtual screen per remote machine. The `name` of each virtual screen must match the hostname of the Hydra instance running on that machine.
- `x`, `y`, `width`, `height` — screen dimensions. Set `width`/`height` to 0 to auto-detect.
- Cursor exits the **right** edge of the first screen to reach the next screen in the list. Additional layout directions are not yet configurable.

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

## Networking with Styx

For machines on different networks, **Styx** is a relay server that securely tunnels Hydra connections.

### Running Styx

```bash
docker run -e RELAY_PASSWORD=<secret> -p 5000:5000 ghcr.io/pacanimal/styx:latest
```

Or build from source:

```bash
docker build -f Styx/Dockerfile -t styx:local .
docker run -e RELAY_PASSWORD=<secret> -p 5000:5000 styx:local
```

### Generating a network config

Open `http://<your-styx-host>:5000` in a browser, enter the relay password, and click **Generate**. Copy the config string.

### Connecting Hydra to Styx

Add `networkConfig` and `hostName` to `hydra.conf`:

```json
{
  "logLevel": "info",
  "hostName": "my-macbook",
  "networkConfig": "<base64 string from the Styx web UI>",
  "screens": [
    { "name": "main",       "x": 0, "y": 0, "width": 0, "height": 0, "isVirtual": false },
    { "name": "my-desktop", "x": 0, "y": 0, "width": 0, "height": 0, "isVirtual": true  }
  ]
}
```

- Both machines must use the **same** network config string (generated once from Styx).
- The `name` of each virtual screen must match the `hostName` of the Hydra instance on that machine.
- Traffic between Hydra instances is end-to-end encrypted — Styx only routes opaque bytes.

## Building from source

```bash
dotnet build Hydra.sln
dotnet test Hydra.sln
```

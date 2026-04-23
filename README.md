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
- **Clipboard sync** — text and images synced automatically when switching between machines (all platforms)
- **File transfer** — cross-machine copy/paste of files and folders via hotkey (macOS and Windows)
- **Media key forwarding** — volume, playback, brightness keys forwarded to the active machine
- **Screensaver sync** — activating your screensaver on the master locks all connected slaves too
- End-to-end encrypted relay via **Styx** for machines on different networks
- macOS, Windows, and Linux (x64 and arm64) support
- **Remote-only mode** — use a headless Linux machine (e.g. Raspberry Pi) as a dedicated input forwarder; all input goes straight to a remote machine with no local screen involved

## Requirements

- .NET 10
- **macOS**: Accessibility permission (System Settings → Privacy & Security → Accessibility)
- **Linux (with display)**: X11 with XInput2 (Wayland not yet supported)
- **Linux (headless/console)**: `remoteOnly: true` in config; user must be in the `input` group (`sudo usermod -aG input $USER`) for `/dev/input/event*` access; `libxkbcommon` installed (`apt install libxkbcommon0`)

## Configuration

Edit `hydra.conf` (sits next to the binary):

```json
{
  "name": "laptop",
  "logLevel": "info",
  "profiles": [
    {
      "profileName": "Home",
      "mode": "Master",
      "hosts": [
        {
          "name": "laptop",
          "neighbours": [
            { "direction": "right", "name": "desktop" }
          ]
        }
      ]
    }
  ]
}
```

Neighbours are **mirrored by default** — declaring that `laptop` has `desktop` to the right automatically creates the reverse: `desktop` has `laptop` to its left. You only need to declare one side. Both sides can still be declared explicitly if needed (the mirror is skipped if the reverse already exists).

### Config fields

**Root-level** (global, apply to all profiles):

- `name` — this machine's name on the network. Optional — defaults to the machine's hostname without domain. Must match one of the host names for the master to identify its own screen.
- `logLevel` — `trce`, `dbug`, `info`, `warn`, `fail`, or `crit`
- `autoUpdate` — `false` to disable automatic updates
- `lockFile` — path to a lock file to prevent multiple instances (default: none)
- `profiles` — array of profile objects (see below); at least one required

**Per-profile** (inside a `profiles` entry):

- `profileName` — name for this profile, logged at startup so you know which one is active (no duplicates allowed)
- `mode` — `Master` or `Slave`
- `networkConfig` — base64 relay config string from the Styx web UI; use this to connect to a standalone Styx server
- `embeddedStyx` — connect to a Styx server using plain-text credentials: `{ "server": "http://<host>:<port>", "password": "<password>" }` — a more readable alternative to copying the base64 `networkConfig` blob
- `embeddedStyxServer` — run a Styx relay server embedded inside this Hydra process: `{ "port": <port>, "password": "<password>" }` — useful for home setups where you don't want a separate Styx container; the machine running this automatically connects to its own server, and other machines connect to it using `embeddedStyx`
- `hosts` — list of host entries for the neighbour graph (master only; slaves don't need this)
- `screenDefinitions` — per-screen scale config (slave only; reported to master via ScreenInfo)
- `mouseScale` — fallback cursor speed multiplier for all screens on this slave (slave only)
- `deadCorners` — pixel dead zone at screen corners where transitions are blocked (default `0`, `50` is a reasonable starting value). Scaled by the screen's mouseScale. Can also be set per-host to override.
- `remoteOnly` — `true` to forward all input to remote machines immediately at startup, with no local screen involved (see [Remote-only mode](#remote-only-mode))
- `syncScreensaver` — `false` to disable screensaver synchronisation (default: `true`)
- `conditions` — optional object; if set, this profile only activates when **all** specified conditions are met (see [Network-aware config](#network-aware-config))
  - `ssid` — activates when connected to this WiFi network name (case-insensitive)
  - `screenCount` — activates when exactly this many screens are connected (integer ≥ 1)

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

Set at the profile level to apply to all hosts:

```json
{
  "profiles": [
    { "mode": "Master", "deadCorners": 50, "hosts": [...] }
  ]
}
```

Override per-host (takes precedence over the profile value):

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

`screenDefinitions` is **slave only**. The slave reports its screen layout and scale settings to the master at connection time; the master applies the scale when routing cursor movement to that slave's screens.

Each entry specifies one or more match criteria — all specified criteria must match (case-insensitive). Use the identifiers shown at startup to build match entries.

```json
{
  "profiles": [
    {
      "mode": "Slave",
      "mouseScale": 1.5,
      "screenDefinitions": [
        { "displayName": "DELL U2720Q",             "mouseScale": 1.5 },
        { "displayName": "Built-in Retina Display", "mouseScale": 1.0 },
        { "outputName": "HDMI-1",                   "mouseScale": 0.8 }
      ]
    }
  ]
}
```

| Field | Default | Description |
|-------|---------|-------------|
| `displayName` | — | Match by display/monitor name (e.g. `"DELL U2720Q"`) |
| `outputName` | — | Match by output connector name (e.g. `"HDMI-1"`) |
| `platformId` | — | Match by platform-specific ID |
| `mouseScale` | — | Cursor speed multiplier on this screen; overrides the profile-level `mouseScale` |

The profile-level `mouseScale` sets a fallback multiplier for all screens on this slave. Per-screen `mouseScale` in a `screenDefinitions` entry overrides it. If neither is set, the multiplier defaults to `1.0`.

At least one match field must be set per `screenDefinitions` entry.

### Network-aware config

If you move your machine between networks (e.g. home vs. office), add multiple profiles to `hydra.conf`, each gated on the current network. Hydra picks the matching profile and restarts automatically when the network changes. If no profile matches — for example, you're at a coffee shop — Hydra idles silently. This is intentional: there are no machines to connect to anyway.

Each profile has a `profileName` — logged at startup so you always know which profile is active.

- `conditions: { "ssid": "..." }` — activates when connected to the named WiFi network (case-insensitive)
- `conditions: { "screenCount": 2 }` — activates when exactly 2 screens are connected
- Conditions are **AND-ed** — `{ "ssid": "Office", "screenCount": 2 }` requires both to match simultaneously
- No `conditions` (or `{}`) — fallback, activates when no other profile matches

A fallback profile is optional. Without one, Hydra idles when no profile matches — e.g. at a coffee shop.

Rules: at most one fallback, no two profiles with identical condition tuples, no duplicate profile names — validated at startup.

Hydra re-evaluates conditions automatically when the network changes or screens are connected/disconnected, and restarts with the appropriate profile if needed.

**Example — laptop as slave at home, master at work:**

A common setup: at home your stationary desktop controls your laptop (the laptop is a slave). At work, your laptop is docked and controls a dedicated workstation (the laptop is the master).

```json
{
  "name": "laptop",
  "profiles": [
    {
      "profileName": "Home",
      "conditions": { "ssid": "HomeWifi" },
      "mode": "Slave",
      "networkConfig": "<base64 string>"
    },
    {
      "profileName": "Work",
      "conditions": { "ssid": "OfficeWifi" },
      "mode": "Master",
      "networkConfig": "<base64 string>",
      "hosts": [
        {
          "name": "laptop",
          "neighbours": [{ "direction": "right", "name": "workstation" }]
        },
        { "name": "workstation" }
      ]
    }
  ]
}
```

**Example — different layouts for docked vs. laptop-only:**

```json
{
  "name": "laptop",
  "profiles": [
    {
      "profileName": "Office docked",
      "mode": "Master",
      "conditions": { "ssid": "Office", "screenCount": 2 },
      "hosts": [
        { "name": "laptop", "neighbours": [{ "direction": "right", "name": "desktop" }] }
      ]
    },
    {
      "profileName": "Office undocked",
      "mode": "Master",
      "conditions": { "ssid": "Office", "screenCount": 1 },
      "hosts": [
        { "name": "laptop", "neighbours": [{ "direction": "right", "name": "desktop" }] }
      ]
    },
    { "profileName": "Away", "mode": "Slave" }
  ]
}
```

> **macOS note:** Location Services permission is only requested if at least one config uses `conditions`. Hydra never asks for location permission when running with a single unconditional config.

### Hotkeys

All hotkeys use **Ctrl+Alt+Super** (Super = ⌘ on macOS, Win on Windows) plus one letter.

| Hotkey | Action |
|--------|--------|
| `Ctrl+Alt+Super+L` | Toggle cursor lock — lock to current screen, or unlock to roam freely |
| `Ctrl+Alt+Super+M` | Toggle relative mouse mode on the current remote screen (useful for games) |
| `Ctrl+Alt+Super+C` | Copy selected files/folders to Hydra's cross-machine clipboard (macOS, Windows) |
| `Ctrl+Alt+Super+V` | Paste previously copied files to the current machine |

**Lock in remote-only mode:** since there is no local screen, the lock hotkey acts as a **remote toggle** — press once to temporarily pass input through to the physical machine running Hydra, press again to re-lock to remote.

**Relative mouse:** relative mode sends mouse deltas instead of absolute coordinates — useful for games or 3D apps that capture the cursor. Toggled per-screen; an on-screen notification confirms the current state.

---

## Clipboard sync

When you move the cursor to a remote machine, Hydra pushes the local clipboard to it. When you move back, the remote clipboard is pulled to the local machine. This happens automatically — no hotkey needed.

Synced content:
- **Plain text** — all platforms
- **Images (PNG)** — all platforms; Windows also handles DIB format for compatibility with legacy apps

Linux syncs both the `CLIPBOARD` selection and the X11 `PRIMARY` (middle-click) selection.

## File transfer

Copy files and folders between machines using the same muscle memory as a local copy/paste.

1. Select files in Finder (macOS) or Explorer (Windows) — including desktop selections
2. Press **Ctrl+Alt+Super+C** — Hydra copies the paths into its transfer buffer and shows a confirmation
3. Move the cursor to the target machine
4. Press **Ctrl+Alt+Super+V** — the files are transferred and placed in the folder currently open in the file manager on the target

The notification shows how many items were copied (e.g. `3 items copied`). Transfers are compressed and verified with a SHA-256 checksum. Only one transfer can be in flight at a time; a progress panel shows speed and allows cancellation. Transfers are aborted automatically if the screensaver activates or the connection drops.

**Platform support:** macOS and Windows. Linux is not supported as a source or destination.

All transfer topologies work: local → remote, remote → local, and remote → remote (via the relay).

## Screensaver sync

When the screensaver activates on the master, Hydra:
- Returns the cursor to the local screen
- Activates the screensaver on all connected slaves

When the master wakes, it deactivates the screensaver on slaves and restores the cursor to the remote screen it was on before.

Set `syncScreensaver: false` in a profile to disable this behaviour.

## Remote-only mode

Remote-only mode turns Hydra into a dedicated input forwarder: 100% of keyboard and mouse input goes to the configured remote machine(s) immediately at startup, with no edge-crossing required. This is useful for setups like a Raspberry Pi as a wireless keyboard/mouse bridge — input is forwarded to a Mac or PC over the network using the Pi's own keyboard layout.

### When to use it

- A headless Linux machine (no monitor, no display server) needs to forward input
- You want a second computer that is always controlled remotely — no toggle, no edge, just instant forwarding
- You want to use a PC keyboard layout on a Mac without installing any software on the Mac (except for Hydra 🙂)

### Configuration

Set `remoteOnly: true` and list the remote host(s). No local entry for the Pi itself is needed.

```json
{
  "name": "pi",
  "profiles": [
    {
      "mode": "Master",
      "remoteOnly": true,
      "networkConfig": "<base64 string>",
      "hosts": [
        { "name": "mac" }
      ]
    }
  ]
}
```

With multiple remote hosts, add neighbours between them so the cursor can transition across hosts:

```json
{
  "name": "pi",
  "profiles": [
    {
      "mode": "Master",
      "remoteOnly": true,
      "networkConfig": "<base64 string>",
      "hosts": [
        {
          "name": "mac",
          "neighbours": [{ "direction": "right", "name": "win" }]
        },
        { "name": "win" }
      ]
    }
  ]
}
```

### Headless Linux (no display server)

On a console-only Linux machine (no `$DISPLAY`), Hydra automatically uses the evdev input subsystem instead of X11. No Xorg or Wayland is needed.

Requirements:
- User must be in the `input` group: `sudo usermod -aG input $USER` (log out and back in for the group change to take effect)
- `libxkbcommon` installed: `sudo apt install libxkbcommon0`
- Set the keyboard layout via `XKB_DEFAULT_LAYOUT` if not `us`, e.g. `XKB_DEFAULT_LAYOUT=gb ./hydra`

> If `$DISPLAY` is set (X11 is running), Hydra uses X11 regardless of `remoteOnly`.

> If no `$DISPLAY` and `remoteOnly` is not set, Hydra exits with an error — it can't capture input without either a display server or remote-only mode.

## Running

```bash
dotnet run --project Hydra
```

Or build a self-contained single-file executable:

```bash
dotnet publish Hydra --runtime osx-arm64  --self-contained   # macOS Apple Silicon
dotnet publish Hydra --runtime win-x64   --self-contained   # Windows x64
dotnet publish Hydra --runtime linux-x64 --self-contained   # Linux x64
dotnet publish Hydra --runtime linux-arm64 --self-contained # Linux arm64 (e.g. Raspberry Pi)
```

Output lands in `Hydra/bin/Release/net10.0/<rid>/publish/`. The binary bundles the runtime and all assets — nothing else needs to ship alongside it.

## Slave setup

On the slave machine:

```json
{
  "name": "desktop",
  "logLevel": "info",
  "profiles": [
    {
      "profileName": "Home",
      "mode": "Slave",
      "networkConfig": "<same base64 string as master>",
      "screenDefinitions": [
        { "displayName": "DELL U2720Q", "mouseScale": 1.5 }
      ]
    }
  ]
}
```

Slaves do not need a `hosts` section — they receive and replay input from the master. The slave auto-detects all local monitors and reports them to the master so the master knows the full remote screen layout.

## Networking with Styx

For machines on different networks, **Styx** is a relay server that securely tunnels Hydra connections. You can run Styx as a **standalone** server (Docker or from source) or **embedded** directly inside a Hydra process.

### Embedded Styx

If you don't want to run a separate Styx container, you can embed a Styx server directly inside a Hydra process. This is ideal for home setups where one machine acts as a hub.

**On the machine that hosts the relay** (e.g. your desktop), add `embeddedStyxServer` to your profile. Hydra will start Styx on the specified port and connect to it automatically:

```json
{
  "name": "desktop",
  "profiles": [
    {
      "mode": "Master",
      "embeddedStyxServer": { "port": 5000, "password": "my-secret" },
      "hosts": [
        { "name": "desktop", "neighbours": [{ "direction": "right", "name": "laptop" }] }
      ]
    }
  ]
}
```

On startup, Hydra logs how other machines should connect:

```
Embedded Styx relay on port 5000
Remote hosts can connect with: embeddedStyx: {"server": "http://<your-ip>:5000", "password": "<password>"}
```

**On each other machine** (master or slave), use `embeddedStyx` with your hub's IP and the same password — no need to copy a base64 blob:

```json
{
  "name": "laptop",
  "profiles": [
    {
      "mode": "Slave",
      "embeddedStyx": { "server": "http://192.168.1.10:5000", "password": "my-secret" }
    }
  ]
}
```

The `embeddedStyx` property is also an alternative to `networkConfig` for any external Styx server — just point it at the server URL and provide the password instead of copying a base64 string.

### Running standalone Styx

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

### Connecting Hydra to a standalone Styx server

Add `networkConfig` to `hydra.conf` on both machines. Use the same config string on all machines in a network.

**Master** (`hydra.conf`):

```json
{
  "name": "laptop",
  "logLevel": "info",
  "profiles": [
    {
      "profileName": "Home",
      "mode": "Master",
      "networkConfig": "<base64 string from the Styx web UI>",
      "hosts": [
        {
          "name": "laptop",
          "neighbours": [{ "direction": "right", "name": "desktop" }]
        }
      ]
    }
  ]
}
```

**Slave** (`hydra.conf`):

```json
{
  "name": "desktop",
  "logLevel": "info",
  "profiles": [
    {
      "profileName": "Home",
      "mode": "Slave",
      "networkConfig": "<same base64 string>"
    }
  ]
}
```

- Both machines must use the **same** network config string.
- Traffic between Hydra instances is end-to-end encrypted — Styx only routes opaque bytes.

## Building from source

```bash
dotnet build Hydra.sln
dotnet test Hydra.sln
```

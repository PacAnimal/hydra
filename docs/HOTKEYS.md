# Terminal control center — hotkey map

Every hotkey below is an `Alt+<letter>` mnemonic (shown as an underlined letter in the TUI)
unless noted otherwise. Two scopes exist:

- **Global** — reachable from any tab, at any time, because the tab strip itself never
  disables. These seven letters (`O P L C R D H`) are reserved and nothing else in the UI
  may reuse them.
- **Per-tab** — only reachable while that specific tab is the one on screen. A tab's own
  content is disabled while it's backgrounded, so its hotkeys — including ones that don't
  collide with anything — cannot fire from another tab. This is why the same letter (e.g. `V`
  for Validate) safely appears on more than one tab: whichever one is on screen is the only
  one actually listening.

Modal confirmation dialogs (Restart/Shutdown/Start/Save & Restart) are their own exclusive
scope — while one is open it owns all keyboard input, so its `Save`/`Cancel`-style choices are
free to reuse any letter regardless of what the tab underneath uses.

## Global

| Key | Action |
|---|---|
| `Esc` | Quit the TUI (Hydra keeps running) |
| `F5` | Refresh now |
| `F1` | Jump to the Help tab |
| `Alt+O` | Overview tab |
| `Alt+P` | Peers & Screens tab |
| `Alt+L` | Logs tab |
| `Alt+C` | Configuration tab |
| `Alt+R` | Remote tab |
| `Alt+D` | Diagnostics tab |
| `Alt+H` | Help tab |

## Overview

| Key | Action |
|---|---|
| `Alt+E` | Reconnect Relay |
| `Alt+T` | Restart Hydra *(confirm dialog)* |
| `Alt+W` | Shutdown Hydra *(confirm dialog)* |
| `Alt+S` | Start Hydra *(confirm dialog)* |

## Configuration

| Key | Action |
|---|---|
| `Alt+F` | Form mode |
| `Alt+X` | Text mode |
| `Alt+G` | Global section |
| `Alt+I` | Profile section |
| `Alt+Y` | Relay section |
| `Alt+B` | Behaviour section |
| `Alt+E` | Previous profile *(Profile section only)* |
| `Alt+N` | Next profile *(Profile section only)* |
| `Alt+S` | Reveal/Hide Secrets |
| `Alt+V` | Validate |
| `Alt+A` | Save |
| `Alt+T` | Save && Restart *(confirm dialog)* |
| — | Reload — mouse/Tab only, no mnemonic |

## Remote

| Key | Action |
|---|---|
| `Alt+I` | Pair |
| `Alt+N` | Load Config |
| `Alt+V` | Validate |
| `Alt+A` | Save && Apply |

## Notes from the last audit

- `Reload` (Configuration) has no mnemonic: every letter in the word was already claimed by a
  higher-traffic action on the same tab (`Alt+R`/`Alt+E`/`Alt+A`/`Alt+D` are all spoken for).
  Losing a rarely-used secondary shortcut was preferable to an awkward or colliding one.
- `Alt+I`, `Alt+N`, `Alt+V`, `Alt+A` each appear on two different tabs (Configuration and
  Remote) that are never both in the foreground at once — this is intentional letter reuse,
  not an oversight, and is exactly what the per-tab disable makes safe.

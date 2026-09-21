# Hydra TUI screenshot tool

Captures `hydra tui --demo` and renders it to a PNG, for the README and other docs.
This is not a mockup: it runs the actual compiled binary under a real pseudo-terminal, records
the raw bytes it writes, and replays them through [xterm.js](https://xtermjs.org/) — the same
terminal engine VS Code uses — inside a headless browser to get a pixel-accurate screenshot.

`--demo` is a real mode of the TUI itself (see
`Hydra/Management/MockManagementClient.cs`): it renders the exact same UI code against
fabricated data — a fake connected peer, fake traffic counters, fake network interfaces —
instead of talking to a live daemon. Every address it shows comes from RFC 5737's reserved
documentation ranges (`192.0.2.0/24`, `198.51.100.0/24`), so nothing captured here is a real
machine identity or a route anyone could actually reach. That also means this tool needs no
daemon, no config file, and no network of its own — it's just the TUI, on its own.

## Usage

```bash
# from the repo root, build Hydra first
dotnet build Hydra.sln --configuration Release

cd HydraWebConfig/tools/tui-screenshot
npm install
npm run shoot          # capture + screenshot in one step
# → output/hydra-tui.png
```

Or run the two steps separately (useful while iterating on `render.html`):

```bash
npm run capture         # writes output/capture.bin, capture.b64, meta.json
npm run screenshot      # renders output/capture.b64 → output/hydra-tui.png
```

Useful flags for `capture.mjs`:

| Flag | Default | Purpose |
|---|---|---|
| `--bin <path>` | auto-detected `Hydra/bin/{Release,Debug}/net10.0/Hydra` | binary to run |
| `--cols`, `--rows` | 130, 42 | terminal size |
| `--tui-attempts` | 5 | retry budget for the TUI actually drawing (see below) |
| `--tui-capture-ms` | 4000 | how long to record once the TUI starts drawing |
| `--goto <letter>` | none (stays on Overview) | jumps to another tab via its Alt-mnemonic (e.g. `--goto c` for Configuration) partway through the capture window |

## Why this needs retry logic at all

`hydra tui` has occasionally not drawn anything within the capture window when spawned under
a synthetic pty with nothing answering its terminal-capability queries in time. `capture.mjs`
detects this by checking whether the very first bytes written are the alternate-screen-enter
sequence (`\x1b[?1049h`) that only the real TUI writes, and retries the pty spawn if not. This
has been reliable in practice with `--demo` (no daemon or network involved at all); if you're
investigating further and it reproduces on a clean, idle machine, that's worth chasing down at
the Terminal.Gui/pty layer rather than papering over with more retries.

## How the terminal query answering works

Terminal.Gui's console driver asks the terminal a handful of capability questions on startup
(cursor position, window size in characters, foreground/background colour, Kitty keyboard
protocol support, primary device attributes) and waits for answers before it draws anything. A
bare pty with nothing on the other end never answers, so `capture.mjs` answers them itself,
matching what a real terminal emulator would send — see `replyFor()` in `capture.mjs` for the
exact sequences.

## Files

- `capture.mjs` — spawns `hydra tui --demo` under `node-pty` and saves the raw captured bytes.
- `render.html` — loads `@xterm/xterm` from a CDN and replays `capture.b64` into it. Everything
  around the terminal is painted in the terminal's OWN background, so there is no foreign colour
  that a capture could pick up at its edges.
- `screenshot.mjs` — serves `render.html` + the capture over a tiny local HTTP server, loads it in
  headless Chromium via Playwright, captures the wrapper, then trims to the content's bounding box
  and pads exactly 4 pixels of background on every side. The crop is decided from the PIXELS rather
  than from an element's box: an element crop is subject to subpixel layout, and two columns of a
  debug frame once rode into a committed PNG down its left edge that way.
  `Tests/Tui/ScreenshotPaddingTests` holds the committed PNGs to the 4-pixel rule.
- `output/` (gitignored) — everything the two scripts produce.

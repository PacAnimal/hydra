# Hydra TUI integration tests

End-to-end tests for `hydra tui`, driven through a real terminal emulator instead of unit-testing
formatting functions in isolation (see `Tests/Management/HydraTuiTests.cs` for those). This is the
integration layer: it spawns the actual compiled binary, feeds its raw output into a real
[xterm.js](https://xtermjs.org/) `Terminal` running in headless Chromium via Playwright, and asserts
against xterm's *parsed* screen buffer — the same engine that turns ANSI bytes into what a person
actually sees, so a test failure means the UI is actually wrong, not that a regex missed an escape
code.

All tests run against `hydra tui --demo` (see `Hydra/Management/MockManagementClient.cs`) — no real
daemon, config, or network involved.

## Running

```bash
# from the repo root, build Hydra first
dotnet build Hydra.sln --configuration Release

cd HydraWebConfig/tools/tui-tests
npm install
npx playwright install chromium   # first time only
npm test
```

## How it works

- `harness.mjs` — `HydraTui.launch()` spawns `hydra tui --demo` under `node-pty` (reusing the same
  terminal-capability handshake as the screenshot tool, from `../tui-screenshot/pty-helpers.mjs`),
  opens a headless browser page hosting `live.html`, and mirrors the pty's output into a live
  xterm.js `Terminal` there as it arrives.
- Tests drive input by sending raw bytes straight to the pty (`tui.alt('c')`, `tui.key('enter')`,
  `tui.send(...)`) — exactly what a real terminal would send for that keypress — and assert via
  `tui.screenText()` / `tui.waitForText(needle)`, which read xterm's actual rendered screen (one
  string per row), or `tui.cellColor(row, col)` for the highlighting/color rules.
- Every real terminal-emulator quirk (cursor positioning, redraws, box-drawing characters, 24-bit
  color) is handled by xterm.js itself, not by us — we're testing against the same parser a real
  terminal uses, not a hand-rolled approximation of one.

## What's covered

- `navigation.spec.mjs` — every tab reachable via its mnemonic, F1 jumping to Help, a full tour.
- `hotkey-scoping.spec.mjs` — regression coverage for the background-tab hotkey bug found and fixed
  this session (see `docs/HOTKEYS.md`): a tab's hotkeys must be dead while it isn't in the
  foreground, even ones that don't collide with anything.
- `overview-actions.spec.mjs` — Reconnect/Restart/Shutdown/Start and their confirmation dialogs.
- `configuration.spec.mjs` — Form/Text mode toggle, all four sections, the no-mnemonic Reload button.
- `remote.spec.mjs` — the Remote tab's fields and actions.
- `misc.spec.mjs` — Esc quits the process, F5 refreshes, the connection lamp is tinted by state and the detail beside it is not.
- `smoke.spec.mjs` — the single fastest "is anything catastrophically broken" check.

## Adding a test

Import `HydraTui` from `./harness.mjs`, `launch()` it in a `beforeEach`, `close()` it in an
`afterEach`. Prefer `waitForText`/`waitForTextGone` over a fixed `setTimeout` for anything that
should eventually appear; reserve a short fixed delay only for asserting that something did **not**
happen (a background tab's hotkey misfiring, for example) since there's nothing to poll for there.

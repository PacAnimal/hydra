#!/usr/bin/env node
// Captures a real `hydra tui --demo` session (a genuine Terminal.Gui render, not a mockup) by
// spawning it under a real pseudo-terminal (node-pty) and answering the handful of terminal
// capability queries (cursor position, window size, colour, Kitty keyboard protocol, device
// attributes) it sends at startup so it actually draws, then saving the raw captured bytes for
// render.html to replay through xterm.js.
//
// `--demo` (see Hydra/Management/MockManagementClient.cs) renders the exact same UI code against
// fabricated data instead of a live daemon — no real config, no real network, no real machine
// identity, and nothing external to wait on. The capture waits for the frame it wants rather than
// a fixed time, so a slow first launch costs time, never a broken capture — see README.md.

import { mkdirSync, writeFileSync } from 'node:fs';
import { dirname, join, resolve } from 'node:path';
import { fileURLToPath } from 'node:url';
import pty from 'node-pty';
import { hydraBinary, recordPty, spawnHydraTui, waitForDraw } from './pty-helpers.mjs';

const __dirname = dirname(fileURLToPath(import.meta.url));

const args = parseArgs(process.argv.slice(2));
const HYDRA_BIN = hydraBinary();
const OUT_DIR = resolve(args.out ?? join(__dirname, 'output'));
const COLS = Number(args.cols ?? 130);
const ROWS = Number(args.rows ?? 42);
const GOTO = typeof args.goto === 'string' ? args.goto : null;
// covers a freshly built binary's cold first launch
const DRAW_TIMEOUT_MS = 120_000;
// text only that tab shows once it has data, by --goto letter
const TAB_MARKERS = {
  p: 'Local Screens',
  l: 'Connected to Styx relay',
  c: 'Machine Name',
  r: 'Pairing code',
  d: 'Management',
  h: 'Move between controls',
};
// the connected status line, drawn in the same frame as the Overview's data
const CONNECTED = '● Connected';

if (GOTO && !TAB_MARKERS[GOTO]) {
  console.error(`--goto ${GOTO} names no tab; use one of ${Object.keys(TAB_MARKERS).join(', ')}`);
  process.exit(2);
}

mkdirSync(OUT_DIR, { recursive: true });

function parseArgs(argv) {
  const out = {};
  for (let i = 0; i < argv.length; i++) {
    const key = argv[i];
    if (!key.startsWith('--')) continue;
    const name = key.slice(2).replace(/-([a-z])/g, (_, c) => c.toUpperCase());
    const value = argv[i + 1] && !argv[i + 1].startsWith('--') ? argv[++i] : true;
    out[name] = value;
  }
  return out;
}

// Everything the TUI wrote up to the end of the frame that shows the wanted tab: the connected Overview,
// then --goto's tab when given. Output past that frame's end is a later frame still being written.
async function captureTui() {
  const term = spawnHydraTui({ pty, bin: HYDRA_BIN, cols: COLS, rows: ROWS });
  const recording = recordPty(term);
  try {
    let end = await waitForDraw(recording, CONNECTED, { timeoutMs: DRAW_TIMEOUT_MS });
    if (GOTO) {
      term.write(`\x1b${GOTO}`);
      end = await waitForDraw(recording, TAB_MARKERS[GOTO], { timeoutMs: DRAW_TIMEOUT_MS, from: end });
    }
    return recording.output.slice(0, end);
  } finally {
    if (!recording.exited) term.kill();
  }
}

async function main() {
  console.error(`[setup] binary: ${HYDRA_BIN}`);
  const bytes = await captureTui();
  console.error(`[tui] captured ${Buffer.byteLength(bytes, 'utf8')} bytes`);

  const binPath = join(OUT_DIR, 'capture.bin');
  const b64Path = join(OUT_DIR, 'capture.b64');
  writeFileSync(binPath, bytes, 'utf8');
  writeFileSync(b64Path, Buffer.from(bytes, 'utf8').toString('base64'));
  writeFileSync(join(OUT_DIR, 'meta.json'), JSON.stringify({ cols: COLS, rows: ROWS }, null, 2));
  console.error(`[done] wrote ${binPath} and ${b64Path}`);
}

main().catch((err) => {
  console.error(err);
  process.exit(1);
});

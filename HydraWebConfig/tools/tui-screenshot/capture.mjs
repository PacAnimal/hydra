#!/usr/bin/env node
// Captures a real `hydra tui --demo` session (a genuine Terminal.Gui render, not a mockup) by
// spawning it under a real pseudo-terminal (node-pty) and answering the handful of terminal
// capability queries (cursor position, window size, colour, Kitty keyboard protocol, device
// attributes) it sends at startup so it actually draws, then saving the raw captured bytes for
// render.html to replay through xterm.js.
//
// `--demo` (see Hydra/Management/MockManagementClient.cs) renders the exact same UI code against
// fabricated data instead of a live daemon — no real config, no real network, no real machine
// identity, and nothing external to wait on, so this capture needs no daemon and no retry logic
// around one. The only flakiness left to retry around is the TUI draw itself occasionally not
// happening on the first attempt under a synthetic pty — see README.md.

import { mkdirSync, writeFileSync } from 'node:fs';
import { dirname, join, resolve } from 'node:path';
import { fileURLToPath } from 'node:url';
import pty from 'node-pty';
import { findDefaultBinary, replyFor, isRealTui } from './pty-helpers.mjs';

const __dirname = dirname(fileURLToPath(import.meta.url));
const REPO_ROOT = resolve(__dirname, '..', '..', '..');

const args = parseArgs(process.argv.slice(2));
const HYDRA_BIN = args.bin ?? findDefaultBinary(REPO_ROOT);
const OUT_DIR = resolve(args.out ?? join(__dirname, 'output'));
const COLS = Number(args.cols ?? 130);
const ROWS = Number(args.rows ?? 42);
const TUI_ATTEMPTS = Number(args.tuiAttempts ?? 5);
const TUI_CAPTURE_MS = Number(args.tuiCaptureMs ?? 4000);
const GOTO = typeof args.goto === 'string' ? args.goto : null;

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

function sleep(ms) {
  return new Promise((r) => setTimeout(r, ms));
}

async function captureTui() {
  for (let attempt = 1; attempt <= TUI_ATTEMPTS; attempt++) {
    console.error(`[tui] attempt ${attempt}/${TUI_ATTEMPTS}`);
    const bytes = await new Promise((resolveCapture) => {
      const term = pty.spawn(HYDRA_BIN, ['tui', '--demo'], {
        name: 'xterm-256color',
        cols: COLS,
        rows: ROWS,
        env: { ...process.env, TERM: 'xterm-256color' },
      });

      let out = '';
      term.onData((data) => {
        out += data;
        const reply = replyFor(data, COLS, ROWS);
        if (reply) term.write(reply);
      });

      // --goto <letter> jumps to another tab via its Alt-mnemonic, once the initial draw has
      // definitely happened, so the capture ends on that tab instead of Overview.
      if (GOTO) setTimeout(() => term.write(`\x1b${GOTO}`), Math.min(1500, TUI_CAPTURE_MS / 2));

      setTimeout(() => {
        try {
          term.kill();
        } catch {
          // already gone
        }
        resolveCapture(out);
      }, TUI_CAPTURE_MS);
    });

    if (isRealTui(bytes)) {
      console.error(`[tui] captured ${Buffer.byteLength(bytes, 'utf8')} bytes on attempt ${attempt}`);
      return bytes;
    }
    console.error(`[tui] attempt ${attempt} did not draw the TUI; retrying`);
    await sleep(300);
  }

  throw new Error(`hydra tui --demo did not draw a real terminal UI after ${TUI_ATTEMPTS} attempts`);
}

async function main() {
  console.error(`[setup] binary: ${HYDRA_BIN}`);
  const bytes = await captureTui();

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

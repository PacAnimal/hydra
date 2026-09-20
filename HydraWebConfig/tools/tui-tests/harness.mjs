// Drives a real `hydra tui --demo` process end-to-end: node-pty spawns the actual binary,
// its output is mirrored live into a real xterm.js Terminal running in a headless Chromium
// page (Playwright), and tests assert against xterm's *parsed* screen buffer — the same
// engine that turns "hydra tui"'s raw ANSI into what a person actually sees. This is the one
// component that decides whether a byte stream reads as a real terminal or as noise, so it's
// the right thing to drive tests through rather than regex-matching escape codes ourselves.

import { createServer } from 'node:http';
import { readFile } from 'node:fs/promises';
import { dirname, join, resolve } from 'node:path';
import { fileURLToPath } from 'node:url';
import pty from 'node-pty';
import { chromium } from 'playwright';
import { findDefaultBinary, replyFor, isRealTui } from '../tui-screenshot/pty-helpers.mjs';

const __dirname = dirname(fileURLToPath(import.meta.url));
const REPO_ROOT = resolve(__dirname, '..', '..', '..');
const XTERM_JS = resolve(__dirname, 'node_modules', '@xterm', 'xterm', 'lib', 'xterm.js');

const MIME = { '.html': 'text/html', '.js': 'text/javascript' };

export class HydraTui {
  static async launch({ color = false, args = [], cols = 130, rows = 42, bin } = {}) {
    const harness = new HydraTui(cols, rows);
    await harness._start({ color, args, bin });
    return harness;
  }

  constructor(cols, rows) {
    this.cols = cols;
    this.rows = rows;
    this._pending = '';
  }

  async _start({ color, args, bin }) {
    this.hydraBin = bin ?? findDefaultBinary(REPO_ROOT);
    this._tuiArgs = ['tui', '--demo', ...(color ? ['--color'] : []), ...args];

    this._server = createServer(async (req, res) => {
      const path = req.url === '/' ? '/live.html' : req.url.split('?')[0];
      const filePath = path === '/xterm.js' ? XTERM_JS : join(__dirname, path);
      try {
        res.writeHead(200, { 'Content-Type': MIME[filePath.slice(filePath.lastIndexOf('.'))] ?? 'application/octet-stream' });
        res.end(await readFile(filePath));
      } catch {
        res.writeHead(404);
        res.end('not found');
      }
    });
    await new Promise((r) => this._server.listen(0, '127.0.0.1', r));
    const port = this._server.address().port;

    this._browser = await chromium.launch();
    this.page = await this._browser.newPage();
    await this.page.goto(`http://127.0.0.1:${port}/live.html?cols=${this.cols}&rows=${this.rows}`);
    await this.page.waitForFunction(() => window.__termReady === true);

    // hydra tui has an occasional first-attempt dispatch flakiness unrelated to rendering
    // (see HydraWebConfig/tools/tui-screenshot/README.md) — retry a couple of times rather
    // than let it flake a whole test run.
    const attempts = 3;
    for (let attempt = 1; attempt <= attempts; attempt++) {
      await this._spawn();
      try {
        await this.waitForText('Hydra Control Center', 4000);
        return;
      } catch (err) {
        await this._killPty();
        if (attempt === attempts) throw err;
      }
    }
  }

  async _spawn() {
    this._alive = true;
    this._raw = '';
    this._pending = '';
    this.pty = pty.spawn(this.hydraBin, this._tuiArgs, {
      name: 'xterm-256color',
      cols: this.cols,
      rows: this.rows,
      env: { ...process.env, TERM: 'xterm-256color' },
    });
    this.pty.onData((data) => {
      this._raw += data;
      const reply = replyFor(data, this.cols, this.rows);
      if (reply) this.pty.write(reply);
      this._pending += data;
    });
    this.pty.onExit(() => {
      this._alive = false;
    });
    // A pty that never draws the real alt-screen (see isRealTui) is the known flaky case —
    // fail fast on it instead of waiting out the full text timeout for nothing.
    await this._waitFor(() => isRealTui(this._raw) || !this._alive, 'the real TUI to draw (alt-screen sequence)', 2000).catch(() => {});
  }

  async _killPty() {
    try {
      if (this._alive) this.pty.kill();
    } catch {
      /* already gone */
    }
  }

  // Pushes everything received from the pty since the last flush into the live terminal.
  // Call before any assertion — screenText()/waitForText() call this for you.
  async flush() {
    if (!this._pending) return;
    const chunk = this._pending;
    this._pending = '';
    await this.page.evaluate((s) => window.__writeToTerm(s), chunk);
  }

  /** Sends raw bytes to the pty, exactly as a real terminal would from a keypress. */
  send(bytes) {
    if (!this._alive) throw new Error('hydra tui process has already exited');
    this.pty.write(bytes);
  }

  /** Alt+<letter> — the mnemonic convention this TUI uses for tab/section navigation. */
  alt(letter) {
    this.send(`\x1b${letter}`);
  }

  key(name) {
    const codes = {
      enter: '\r',
      escape: '\x1b',
      tab: '\t',
      shiftTab: '\x1b[Z',
      up: '\x1b[A',
      down: '\x1b[B',
      right: '\x1b[C',
      left: '\x1b[D',
      // F-key escape sequences for xterm-256color's terminfo (SS3 for F1-F4, CSI-tilde beyond).
      f1: '\x1bOP',
      f2: '\x1bOQ',
      f3: '\x1bOR',
      f4: '\x1bOS',
      f5: '\x1b[15~',
    };
    this.send(codes[name] ?? name);
  }

  async screenText() {
    await this.flush();
    return this.page.evaluate(() => window.__screenText());
  }

  async cellColor(row, col) {
    await this.flush();
    return this.page.evaluate(([r, c]) => window.__cellColor(r, c), [row, col]);
  }

  async _waitFor(predicate, description, timeout) {
    const deadline = Date.now() + timeout;
    while (Date.now() < deadline) {
      if (predicate()) return;
      await new Promise((r) => setTimeout(r, 50));
    }
    throw new Error(`Timed out waiting for ${description}`);
  }

  /** Polls the rendered screen until it contains `needle`, or throws with the last screen shown. */
  async waitForText(needle, timeout = 3000) {
    const deadline = Date.now() + timeout;
    let last = '';
    while (Date.now() < deadline) {
      await this.flush();
      last = await this.page.evaluate(() => window.__screenText());
      if (last.includes(needle)) return last;
      await new Promise((r) => setTimeout(r, 50));
    }
    throw new Error(`Timed out waiting for ${JSON.stringify(needle)} on screen. Last screen:\n${last}`);
  }

  /** Polls until `needle` disappears from the rendered screen. */
  async waitForTextGone(needle, timeout = 3000) {
    const deadline = Date.now() + timeout;
    while (Date.now() < deadline) {
      await this.flush();
      const screen = await this.page.evaluate(() => window.__screenText());
      if (!screen.includes(needle)) return;
      await new Promise((r) => setTimeout(r, 50));
    }
    throw new Error(`Timed out waiting for ${JSON.stringify(needle)} to disappear from the screen`);
  }

  async waitForExit(timeout = 3000) {
    await this._waitFor(() => !this._alive, 'the hydra process to exit', timeout);
  }

  async close() {
    try {
      if (this._alive) this.pty.kill();
    } catch {
      /* already gone */
    }
    try {
      await this._browser?.close();
    } catch {
      /* best effort */
    }
    try {
      this._server?.close();
    } catch {
      /* best effort */
    }
  }
}

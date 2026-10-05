// Drives a real `hydra tui --demo` process end-to-end: node-pty spawns the actual binary,
// its output is mirrored live into a real xterm.js Terminal running in a headless Chromium
// page (Playwright), and tests assert against xterm's *parsed* screen buffer — the same
// engine that turns "hydra tui"'s raw ANSI into what a person actually sees. This is the one
// component that decides whether a byte stream reads as a real terminal or as noise, so it's
// the right thing to drive tests through rather than regex-matching escape codes ourselves.

import { dirname } from 'node:path';
import { fileURLToPath } from 'node:url';
import pty from 'node-pty';
import { test as base, chromium } from '@playwright/test';
import { hydraBinary, serveStatic, spawnHydraTui, terminalFiles } from '../tui-screenshot/pty-helpers.mjs';

const __dirname = dirname(fileURLToPath(import.meta.url));
// how long a pty that has not yet drawn a frame must be quiet before its output is shown anyway
const IDLE_FLUSH_MS = 150;
// a warm launch, see warm-up.setup.mjs for the cold one
const LAUNCH_TIMEOUT_MS = 10_000;
// frame markers, see flush()
const HIDE_CURSOR = '\x1b[?25l';
const SHOW_CURSOR = '\x1b[?25h';
const hasFrameMarker = (s) => s.includes(HIDE_CURSOR) || s.includes(SHOW_CURSOR);
// text only the Help tab shows
const HELP_MARKER = 'Move between controls';

export class HydraTui {
  static async launch({ args = [], cols = 130, rows = 42 } = {}) {
    const harness = new HydraTui(cols, rows);
    try {
      await harness._start({ args });
    } catch (err) {
      // nobody gets a harness to close, so the browser and server it already opened go here
      await harness.close();
      throw err;
    }
    return harness;
  }

  constructor(cols, rows) {
    this.cols = cols;
    this.rows = rows;
    this._pending = '';
  }

  async _start({ args }) {
    this._server = await serveStatic({ roots: [__dirname], files: terminalFiles(__dirname), index: 'live.html' });

    this._browser = await chromium.launch();
    this.page = await this._browser.newPage();
    await this.page.goto(`${this._server.origin}/live.html?cols=${this.cols}&rows=${this.rows}`);
    await this.page.waitForFunction(() => window.__termReady === true);

    this._spawn(hydraBinary(), args);
    await this.waitForText('Hydra Control Center', LAUNCH_TIMEOUT_MS);
  }

  _spawn(bin, args) {
    this._alive = true;
    this._raw = '';
    this._sawFrame = false;
    this.pty = spawnHydraTui({
      pty,
      bin,
      args,
      cols: this.cols,
      rows: this.rows,
      onData: (data) => {
        this._raw += data;
        this._pending += data;
        this._sawFrame ||= hasFrameMarker(this._raw);
        this._settled = false;
        clearTimeout(this._idleTimer);
        this._idleTimer = setTimeout(() => (this._settled = true), IDLE_FLUSH_MS);
      },
    });
    this.pty.onExit(() => {
      this._alive = false;
      clearTimeout(this._idleTimer);
    });
  }

  // Pushes every complete frame received from the pty since the last flush into the live terminal.
  // Call before any assertion — screenText()/waitForText() call this for you.
  //
  // Only whole frames: Terminal.Gui ends each one by setting the cursor's visibility (ESC[?25l or
  // ESC[?25h) and never emits that mid-frame, so output past the last such marker is a frame still
  // being written. Showing it would let a test see the new page's first rows over the old page's rest.
  // Output that never gets a marker, such as an error printed before the TUI starts, is shown once
  // the pty has exited, or gone quiet before drawing any frame. Once frames are being drawn, a quiet
  // pty may just be a slow one midway through the next, so only a marker ends it.
  async flush() {
    const marker = Math.max(this._pending.lastIndexOf(HIDE_CURSOR), this._pending.lastIndexOf(SHOW_CURSOR));
    const whole = !this._alive || (this._settled && !this._sawFrame);
    const end = whole ? this._pending.length : marker < 0 ? 0 : marker + HIDE_CURSOR.length;
    if (end === 0) return;
    const chunk = this._pending.slice(0, end);
    this._pending = this._pending.slice(chunk.length);
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

  /**
   * Waits until every key sent so far has been handled, for asserting that one did nothing. Input is
   * processed in order, so once the Help tab that F1 opens is on screen, so is whatever came before it.
   *
   * That proves only that the keys were handled synchronously: an effect a key starts asynchronously can
   * still be on its way, so wait for such effects directly. It leaves you on the Help tab, and throws if
   * Help is already showing, since then the screen it waits for would prove nothing.
   */
  async afterInput() {
    if ((await this.screenText()).includes(HELP_MARKER)) throw new Error('afterInput() needs a screen other than Help to wait from');
    this.key('f1');
    return this.waitForText(HELP_MARKER);
  }

  /** Switches tab by its mnemonic and waits for `marker`, a text only that tab shows. */
  gotoTab(letter, marker) {
    this.alt(letter);
    return this.waitForText(marker);
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
      if (!this._alive) throw new Error(`hydra tui exited before showing ${JSON.stringify(needle)}. Last screen:\n${last}`);
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
      // a dead tui clears its screen, so absence proves nothing once it has exited
      if (!this._alive) throw new Error(`hydra tui exited while waiting for ${JSON.stringify(needle)} to go. Last screen:\n${screen}`);
      if (!screen.includes(needle)) return;
      await new Promise((r) => setTimeout(r, 50));
    }
    throw new Error(`Timed out waiting for ${JSON.stringify(needle)} to disappear from the screen`);
  }

  async waitForExit(timeout = 3000) {
    await this._waitFor(() => !this._alive, 'the hydra process to exit', timeout);
  }

  async close() {
    clearTimeout(this._idleTimer);
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

/** A demo TUI that has drawn its connected Overview, closed after the test whatever happens. */
export const test = base.extend({
  tui: async ({}, use) => {
    const tui = await HydraTui.launch();
    try {
      await tui.waitForText('Connected');
      await use(tui);
    } finally {
      await tui.close();
    }
  },
});

export { expect } from '@playwright/test';

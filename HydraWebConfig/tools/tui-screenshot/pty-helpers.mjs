// Shared between the screenshot tool and the tui-tests suite — both drive `hydra tui --demo` under a
// real node-pty, need the same terminal-capability handshake, and serve an xterm.js page to replay it.
// Node builtins only: each tool installs its own node_modules, so this file must not need one.

import { existsSync } from 'node:fs';
import { readFile } from 'node:fs/promises';
import { createServer } from 'node:http';
import { dirname, extname, join, resolve, sep } from 'node:path';
import { fileURLToPath } from 'node:url';

const HERE = dirname(fileURLToPath(import.meta.url));
const REPO_ROOT = resolve(HERE, '..', '..', '..');

// The binary every tool runs: $HYDRA_TUI_BIN when set, else the repo's Release build, else its Debug one.
export function hydraBinary() {
  const override = process.env.HYDRA_TUI_BIN;
  if (override) {
    if (!existsSync(override)) throw new Error(`HYDRA_TUI_BIN names ${override}, which does not exist.`);
    return resolve(override);
  }
  const candidates = [
    join(REPO_ROOT, 'Hydra', 'bin', 'Release', 'net10.0', 'Hydra'),
    join(REPO_ROOT, 'Hydra', 'bin', 'Debug', 'net10.0', 'Hydra'),
  ];
  const hit = candidates.find(existsSync);
  if (!hit) {
    throw new Error(
      'Could not find a built Hydra binary. Run "dotnet build Hydra.sln" (or --configuration Release) ' +
        'from the repo root first, or set HYDRA_TUI_BIN.\nLooked for:\n' +
        candidates.map((c) => `  ${c}`).join('\n'),
    );
  }
  return resolve(hit);
}

// Terminal.Gui asks the terminal a handful of capability questions on startup (cursor
// position, window size in chars, foreground/background colour, Kitty keyboard protocol
// support, primary device attributes). A pty with nothing on the other end never answers,
// so this mimics a well-behaved terminal that does. Unanswered, the window-size query leaves
// it waiting forever without drawing anything.
const QUERY = /\x1b\[6n|\x1b\[18t|\x1b\]1[01];\?\x1b\\|\x1b\[\?u|\x1b\[0c/g;
// longest query above, so a tail this short can only be the start of one
const LONGEST_QUERY = 8;

export function replyTo(query, cols, rows) {
  switch (query) {
    case '\x1b[6n':
      return '\x1b[1;1R';
    case '\x1b[18t':
      return `\x1b[8;${rows};${cols}t`;
    case '\x1b]10;?\x1b\\':
      return '\x1b]10;rgb:e0e0/e0e0/e0e0\x1b\\';
    case '\x1b]11;?\x1b\\':
      return '\x1b]11;rgb:1e1e/1e1e/1e1e\x1b\\';
    case '\x1b[?u':
      return '\x1b[?0u';
    case '\x1b[0c':
      return '\x1b[?62;1;2;6;9;15;18;21;22c';
    default:
      throw new Error(`no reply for terminal query ${JSON.stringify(query)}`);
  }
}

// Answers the queries in a pty's output as it arrives. A read can end partway through a query, so
// the unanswered tail of each chunk is carried into the next instead of being lost with it.
export function queryResponder(cols, rows) {
  let carry = '';
  return (data) => {
    const buf = carry + data;
    let reply = '';
    let end = 0;
    for (const match of buf.matchAll(QUERY)) {
      reply += replyTo(match[0], cols, rows);
      end = match.index + match[0].length;
    }
    carry = buf.slice(Math.max(end, buf.length - (LONGEST_QUERY - 1)));
    return reply;
  };
}

// Terminal.Gui ends every frame by setting the cursor's visibility, and never does so mid-frame.
export const FRAME_MARKERS = ['\x1b[?25l', '\x1b[?25h'];
// an OSC sequence: the window title, colour queries
const OSC = /\x1b\][^\x07\x1b]*(?:\x07|\x1b\\)/g;

// The offset in `raw` just past the end of the first frame, at or after `from`, that drew `needle`; -1 if
// none has yet. Text inside OSC sequences is not drawn: the window title carries the TUI's name long before
// anything is on screen.
export function drawnFrameEnd(raw, needle, from = 0) {
  const visible = raw.replace(OSC, (osc) => '\0'.repeat(osc.length));
  const at = visible.indexOf(needle, from);
  if (at < 0) return -1;
  const ends = FRAME_MARKERS.map((m) => visible.indexOf(m, at + needle.length)).filter((i) => i >= 0);
  return ends.length ? Math.min(...ends) + FRAME_MARKERS[0].length : -1;
}

// Records everything a pty writes, for waitForDraw.
export function recordPty(term) {
  const recording = { output: '', exited: false, listeners: new Set() };
  const notify = () => recording.listeners.forEach((listener) => listener());
  term.onData((data) => {
    recording.output += data;
    notify();
  });
  term.onExit(() => {
    recording.exited = true;
    notify();
  });
  return recording;
}

// Resolves with drawnFrameEnd's offset once a frame at or after `from` has drawn `needle`. Rejects when the
// process exits first, or at the deadline, with the tail of what it did write.
export function waitForDraw(recording, needle, { timeoutMs, from = 0 }) {
  return new Promise((resolvePromise, reject) => {
    const settle = (fn, value) => {
      recording.listeners.delete(check);
      clearTimeout(timer);
      fn(value);
    };
    const fail = (why) => settle(reject, new Error(`${why} drawing ${JSON.stringify(needle)}. Output:\n${JSON.stringify(recording.output.slice(-2000))}`));
    const check = () => {
      const end = drawnFrameEnd(recording.output, needle, from);
      if (end >= 0) settle(resolvePromise, end);
      else if (recording.exited) fail('hydra tui exited before');
    };
    const timer = setTimeout(() => fail(`hydra tui took over ${timeoutMs} ms`), timeoutMs);
    recording.listeners.add(check);
    check();
  });
}

// Spawns `hydra tui --demo` and answers its capability queries. `pty` is the caller's own node-pty.
export function spawnHydraTui({ pty, bin, args = [], cols, rows, onData = () => {} }) {
  const term = pty.spawn(bin, ['tui', '--demo', ...args], {
    name: 'xterm-256color',
    cols,
    rows,
    env: { ...process.env, TERM: 'xterm-256color' },
  });
  const respond = queryResponder(cols, rows);
  term.onData((data) => {
    onData(data);
    const reply = respond(data);
    if (reply) term.write(reply);
  });
  return term;
}

// What a page needs for the Hydra terminal, as serveStatic `files`: xterm.js and its stylesheet from the
// tool's own node_modules, and the shared terminal.js beside this file.
export function terminalFiles(toolDir) {
  const xterm = join(toolDir, 'node_modules', '@xterm', 'xterm');
  return {
    '/xterm.js': join(xterm, 'lib', 'xterm.js'),
    '/xterm.css': join(xterm, 'css', 'xterm.css'),
    '/terminal.js': join(HERE, 'terminal.js'),
  };
}

const MIME = {
  '.html': 'text/html',
  '.js': 'text/javascript',
  '.css': 'text/css',
  '.json': 'application/json',
  '.b64': 'text/plain',
};

// Serves `files` (exact URL path → file) and otherwise the first of `roots` holding the path, on a free
// loopback port. Resolves to the origin URL and a close(). A path that resolves outside its root is
// never served from it, so `..` cannot reach the rest of the disk.
export async function serveStatic({ roots, files = {}, index }) {
  const server = createServer(async (req, res) => {
    let path;
    try {
      path = decodeURIComponent(req.url.split('?')[0]);
    } catch {
      res.writeHead(400);
      res.end('bad path');
      return;
    }
    if (path === '/') path = `/${index}`;
    const candidates = files[path] ? [files[path]] : roots.map((dir) => within(dir, path)).filter(Boolean);
    for (const filePath of candidates) {
      try {
        const body = await readFile(filePath);
        res.writeHead(200, { 'Content-Type': MIME[extname(filePath)] ?? 'application/octet-stream' });
        res.end(body);
        return;
      } catch {
        // try the next root
      }
    }
    res.writeHead(404);
    res.end('not found');
  });
  await new Promise((r) => server.listen(0, '127.0.0.1', r));
  return { origin: `http://127.0.0.1:${server.address().port}`, close: () => server.close() };
}

// the file `path` names under `root`, or null when it would resolve outside it
function within(root, path) {
  const base = resolve(root);
  const full = resolve(base, `.${path}`);
  return full === base || full.startsWith(base + sep) ? full : null;
}

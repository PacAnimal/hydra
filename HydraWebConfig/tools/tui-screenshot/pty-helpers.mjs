// Shared between capture.mjs (screenshot tool) and the tui-tests suite — both drive
// `hydra tui --demo` under a real node-pty and need the same terminal-capability handshake.

import { existsSync } from 'node:fs';
import { join, resolve } from 'node:path';

export function findDefaultBinary(repoRoot) {
  const candidates = [
    join(repoRoot, 'Hydra', 'bin', 'Release', 'net10.0', 'Hydra'),
    join(repoRoot, 'Hydra', 'bin', 'Debug', 'net10.0', 'Hydra'),
  ];
  const hit = candidates.find(existsSync);
  if (!hit) {
    throw new Error(
      'Could not find a built Hydra binary. Run "dotnet build Hydra.sln" (or --configuration Release) ' +
        'from the repo root first, or pass a binary path explicitly.\nLooked for:\n' +
        candidates.map((c) => `  ${c}`).join('\n'),
    );
  }
  return resolve(hit);
}

// Terminal.Gui asks the terminal a handful of capability questions on startup (cursor
// position, window size in chars, foreground/background colour, Kitty keyboard protocol
// support, primary device attributes). A pty with nothing on the other end never answers,
// so this mimics a well-behaved terminal that does.
export function replyFor(buf, cols, rows) {
  const replies = [];
  if (/\x1b\[6n/.test(buf)) replies.push('\x1b[1;1R');
  if (/\x1b\[18t/.test(buf)) replies.push(`\x1b[8;${rows};${cols}t`);
  if (/\x1b\]10;\?\x1b\\/.test(buf)) replies.push('\x1b]10;rgb:e0e0/e0e0/e0e0\x1b\\');
  if (/\x1b\]11;\?\x1b\\/.test(buf)) replies.push('\x1b]11;rgb:1e1e/1e1e/1e1e\x1b\\');
  if (/\x1b\[\?u/.test(buf)) replies.push('\x1b[?0u');
  if (/\x1b\[0c/.test(buf)) replies.push('\x1b[?62;1;2;6;9;15;18;21;22c');
  return replies.join('');
}

// Genuine Terminal.Gui entry writes the alt-screen sequence as its first bytes.
// Anything else at the start means the process never got that far.
export function isRealTui(bytes) {
  return bytes.slice(0, 20).includes('\x1b[?1049h');
}

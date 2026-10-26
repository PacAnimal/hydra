// A freshly built binary is vetted by the OS (and any antivirus) on every launch until that verdict is cached,
// which can hold each launch for over a minute, for minutes after a build. This launches until one draws as fast
// as a warm one does, so the tests' launches find the binary warm and their time measures the TUI.

import pty from 'node-pty';
import { test } from '@playwright/test';
import { hydraBinary, LAUNCH_TIMEOUT_MS, recordPty, spawnHydraTui, waitForDraw } from '../tui-screenshot/pty-helpers.mjs';

// a warm launch draws in well under a second, a vetted one takes tens of seconds
const WARM_LAUNCH_MS = 5_000;
// how long the OS may take to cache its verdict, as a hang guard
const WARM_UP_TIMEOUT_MS = 15 * 60_000;

test('hydra tui --demo launches warm', async () => {
  test.setTimeout(WARM_UP_TIMEOUT_MS);
  for (;;) {
    const started = Date.now();
    const term = spawnHydraTui({ pty, bin: hydraBinary(), cols: 130, rows: 42 });
    const recording = recordPty(term);
    try {
      await waitForDraw(recording, 'Hydra Control Center', { timeoutMs: LAUNCH_TIMEOUT_MS });
    } finally {
      if (!recording.exited) term.kill();
    }
    if (Date.now() - started < WARM_LAUNCH_MS) return;
  }
});

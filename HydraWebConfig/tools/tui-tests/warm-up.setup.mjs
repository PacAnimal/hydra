// The first launch of a freshly built binary is the expensive one: its files are cold and the OS checks
// them on first use, which under load can take longer than a whole test. Paying for that once here keeps
// every test's own launch fast, so its budget measures the TUI rather than the disk.

import pty from 'node-pty';
import { test } from '@playwright/test';
import { hydraBinary, recordPty, spawnHydraTui, waitForDraw } from '../tui-screenshot/pty-helpers.mjs';

const COLD_LAUNCH_TIMEOUT_MS = 120_000;

test('hydra tui --demo draws on its first launch', async () => {
  test.setTimeout(COLD_LAUNCH_TIMEOUT_MS + 10_000);
  const term = spawnHydraTui({ pty, bin: hydraBinary(), cols: 130, rows: 42 });
  const recording = recordPty(term);
  try {
    await waitForDraw(recording, 'Hydra Control Center', { timeoutMs: COLD_LAUNCH_TIMEOUT_MS });
  } finally {
    if (!recording.exited) term.kill();
  }
});

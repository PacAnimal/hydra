// Regression coverage for the background-tab hotkey bug found and fixed this session:
// content inside a tab that isn't in the foreground must never respond to its own mnemonics,
// even when that mnemonic doesn't collide with anything on the tab actually on screen. See
// docs/HOTKEYS.md and SetDescendantsEnabled in HydraTui.cs.

import { test, expect } from '@playwright/test';
import { HydraTui } from './harness.mjs';

test.describe('hotkey scoping', () => {
  let tui;

  test.beforeEach(async () => {
    tui = await HydraTui.launch();
    await tui.waitForText('Connected');
  });

  test.afterEach(async () => {
    await tui.close();
  });

  test('Alt+R from Overview switches to Remote, not Configuration\'s hidden Relay section', async () => {
    // The original bug: this used to pop Configuration's "Save && Restart" confirmation
    // dialog from the Overview tab, because Configuration's content stayed hotkey-reachable
    // while backgrounded.
    tui.alt('r');
    const screen = await tui.waitForText('Pairing code');
    expect(screen).not.toContain('Save and Restart');
    expect(screen).not.toContain('Save hydra.conf and restart Hydra?');
  });

  test('Alt+V (Configuration\'s Validate) does nothing from Overview', async () => {
    const before = await tui.screenText();
    tui.alt('v');
    // give a background hotkey every chance to misfire before asserting it didn't
    await new Promise((r) => setTimeout(r, 300));
    const after = await tui.screenText();
    expect(after).toBe(before);
  });

  test("Configuration's own Validate (Alt+V) does something once Configuration is in the foreground", async () => {
    tui.alt('c');
    await tui.waitForText('Machine Name');
    tui.alt('v');
    // Validate reports through a modal dialog, not the activity line.
    const screen = await tui.waitForText('Configuration is valid');
    expect(screen).toContain('Configuration');
    tui.key('enter'); // dismiss, so the process exits cleanly on close()
  });

  test('switching to Peers & Screens disables Configuration\'s section buttons', async () => {
    tui.alt('c');
    await tui.waitForText('Machine Name');
    tui.alt('p');
    await tui.waitForText('Local Screens');
    const before = await tui.screenText();
    // Alt+Y is Configuration's Relay section — must be inert while Peers & Screens is shown.
    tui.alt('y');
    await new Promise((r) => setTimeout(r, 300));
    const after = await tui.screenText();
    expect(after).toBe(before);
  });

  test('Alt+I reaches Configuration\'s Profile section while Configuration is active', async () => {
    tui.alt('c');
    await tui.waitForText('Machine Name');
    tui.alt('i');
    const screen = await tui.waitForText('Profile Name');
    expect(screen).toContain('SSID');
    expect(screen).toContain('Screen Count');
  });

  test('the same Alt+I reaches Remote\'s Pair button once Remote is active, not Configuration\'s Profile', async () => {
    tui.alt('r');
    await tui.waitForText('Pairing code');
    tui.alt('i');
    // Pair with an empty host/code is a no-op-but-visible attempt, not a silent nothing —
    // the important assertion is that Configuration's "Profile" section text never appears.
    await new Promise((r) => setTimeout(r, 300));
    const screen = await tui.screenText();
    expect(screen).not.toContain('Profile Name');
    expect(screen).toContain('Pairing code');
  });
});

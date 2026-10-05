// Regression coverage for the background-tab hotkey bug: content inside a tab that isn't in the
// foreground must never respond to its own mnemonics, even when that mnemonic doesn't collide with
// anything on the tab actually on screen. See docs/HOTKEYS.md and SetDescendantsEnabled in HydraTui.cs.

import { test, expect } from './harness.mjs';

test.describe('hotkey scoping', () => {
  test('Alt+R from Overview switches to Remote, not Configuration\'s hidden Relay section', async ({ tui }) => {
    // The original bug: this used to pop Configuration's "Save && Restart" confirmation
    // dialog from the Overview tab, because Configuration's content stayed hotkey-reachable
    // while backgrounded.
    const screen = await tui.gotoTab('r', 'Pairing code');
    expect(screen).not.toContain('Save and Restart');
    expect(screen).not.toContain('Save hydra.conf and restart Hydra?');
  });

  test('Alt+V (Configuration\'s Validate) does nothing from Overview', async ({ tui }) => {
    tui.alt('v');
    const screen = await tui.afterInput();
    expect(screen).not.toContain('Configuration is valid');
    expect(screen).not.toContain('Configuration Error');
  });

  test("Configuration's own Validate (Alt+V) does something once Configuration is in the foreground", async ({ tui }) => {
    await tui.gotoTab('c', 'Machine Name');
    tui.alt('v');
    // Validate reports through a modal dialog, not the activity line.
    const screen = await tui.waitForText('Configuration is valid');
    expect(screen).toContain('Configuration');
    tui.key('enter'); // dismiss, so the process exits cleanly on close()
  });

  test('switching to Peers & Screens disables Configuration\'s section buttons', async ({ tui }) => {
    await tui.gotoTab('c', 'Machine Name');
    await tui.gotoTab('p', 'Local Screens');
    // Alt+Y is Configuration's Relay section — must be inert while Peers & Screens is shown.
    tui.alt('y');
    const screen = await tui.gotoTab('c', 'Reload');
    expect(screen).toContain('Machine Name');
    expect(screen).not.toContain('Embedded URL');
  });

  test('Alt+I reaches Configuration\'s Profile section while Configuration is active', async ({ tui }) => {
    await tui.gotoTab('c', 'Machine Name');
    tui.alt('i');
    const screen = await tui.waitForText('Profile Name');
    expect(screen).toContain('SSID');
    expect(screen).toContain('Screen Count');
  });

  test('the same Alt+I reaches Remote\'s Pair button once Remote is active, not Configuration\'s Profile', async ({ tui }) => {
    await tui.gotoTab('r', 'Pairing code');
    tui.alt('i');
    // Pair with an empty host/code asks for them rather than doing nothing, so it is visible.
    const screen = await tui.waitForText('Enter the peer host and its one-time pairing code.');
    expect(screen).not.toContain('Profile Name');
    expect(screen).toContain('Pairing code');
    tui.key('enter');
  });
});

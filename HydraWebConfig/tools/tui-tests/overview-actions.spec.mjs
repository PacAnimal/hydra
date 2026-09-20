import { test, expect } from '@playwright/test';
import { HydraTui } from './harness.mjs';

test.describe('Overview actions', () => {
  let tui;

  test.beforeEach(async () => {
    tui = await HydraTui.launch();
    await tui.waitForText('Connected');
  });

  test.afterEach(async () => {
    await tui.close();
  });

  test('Reconnect Relay (Alt+E) reports progress on the activity line', async () => {
    tui.alt('e');
    const screen = await tui.waitForText('reconnect');
    expect(screen.toLowerCase()).toContain('relay');
  });

  test('Restart Hydra (Alt+T) shows a confirmation dialog defaulting to Cancel', async () => {
    tui.alt('t');
    const screen = await tui.waitForText('Restart the running Hydra process?');
    expect(screen).toContain('Restart Hydra');
    tui.key('enter'); // default focus is Cancel — dismiss without actually restarting
    await tui.waitForTextGone('Restart the running Hydra process?');
  });

  test('Shutdown Hydra (Alt+W) shows a confirmation dialog defaulting to Cancel', async () => {
    tui.alt('w');
    const screen = await tui.waitForText('disconnect all peers');
    expect(screen).toContain('Shutdown Hydra');
    tui.key('enter');
    await tui.waitForTextGone('disconnect all peers');
  });

  test('Start Hydra (Alt+S) is disabled while connected', async () => {
    const before = await tui.screenText();
    tui.alt('s');
    await new Promise((r) => setTimeout(r, 300));
    const after = await tui.screenText();
    expect(after).toBe(before);
  });

  test('canceling Restart leaves the connection state untouched', async () => {
    tui.alt('t');
    await tui.waitForText('Restart the running Hydra process?');
    tui.key('enter'); // Cancel
    await tui.waitForTextGone('Restart the running Hydra process?');
    const screen = await tui.screenText();
    expect(screen).toContain('Connected');
  });
});

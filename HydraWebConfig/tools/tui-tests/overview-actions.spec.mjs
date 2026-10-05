import { test, expect } from './harness.mjs';

test.describe('Overview actions', () => {
  test('Reconnect Relay (Alt+E) reports progress on the activity line', async ({ tui }) => {
    tui.alt('e');
    const screen = await tui.waitForText('reconnect');
    expect(screen.toLowerCase()).toContain('relay');
  });

  test('Restart Hydra (Alt+T) shows a confirmation dialog defaulting to Cancel', async ({ tui }) => {
    tui.alt('t');
    const screen = await tui.waitForText('Restart the running Hydra process?');
    expect(screen).toContain('Restart Hydra');
    tui.key('enter'); // default focus is Cancel — dismiss without actually restarting
    await tui.waitForTextGone('Restart the running Hydra process?');
  });

  test('Shutdown Hydra (Alt+W) shows a confirmation dialog defaulting to Cancel', async ({ tui }) => {
    tui.alt('w');
    const screen = await tui.waitForText('disconnect all peers');
    expect(screen).toContain('Shutdown Hydra');
    tui.key('enter');
    await tui.waitForTextGone('disconnect all peers');
  });

  test('Start Hydra (Alt+S) is disabled while connected', async ({ tui }) => {
    tui.alt('s');
    const screen = await tui.afterInput();
    expect(screen).not.toContain('Start Hydra with the current configuration?');
  });

  test('canceling Restart leaves the connection state untouched', async ({ tui }) => {
    tui.alt('t');
    await tui.waitForText('Restart the running Hydra process?');
    tui.key('enter'); // Cancel
    await tui.waitForTextGone('Restart the running Hydra process?');
    const screen = await tui.screenText();
    expect(screen).toContain('Connected');
  });
});

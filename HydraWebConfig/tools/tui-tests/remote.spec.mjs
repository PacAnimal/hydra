import { test, expect } from '@playwright/test';
import { HydraTui } from './harness.mjs';

test.describe('Remote tab', () => {
  let tui;

  test.beforeEach(async () => {
    tui = await HydraTui.launch();
    await tui.waitForText('Connected');
    tui.alt('r');
    await tui.waitForText('Pairing code');
  });

  test.afterEach(async () => {
    await tui.close();
  });

  test('shows the peer host and pairing code fields with all four actions', async () => {
    const screen = await tui.screenText();
    expect(screen).toContain('Peer host');
    expect(screen).toContain('Pairing code');
    expect(screen).toContain('Pair');
    expect(screen).toContain('Load Config');
    expect(screen).toContain('Validate');
    expect(screen).toContain('Save && Apply');
  });

  test('starts with placeholder guidance text before any peer is selected', async () => {
    const screen = await tui.screenText();
    expect(screen).toContain('Select a peer, pair it locally, then load its redacted');
  });
});

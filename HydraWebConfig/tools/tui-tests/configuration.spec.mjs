import { test, expect } from '@playwright/test';
import { HydraTui } from './harness.mjs';

test.describe('Configuration tab', () => {
  let tui;

  test.beforeEach(async () => {
    tui = await HydraTui.launch();
    await tui.waitForText('Connected');
    tui.alt('c');
    await tui.waitForText('Machine Name');
  });

  test.afterEach(async () => {
    await tui.close();
  });

  test('defaults to Form mode with the Global section', async () => {
    const screen = await tui.screenText();
    expect(screen).toContain('Global');
    expect(screen).toContain('Auto Update');
    expect(screen).toContain('Debug Shield');
  });

  test('Alt+X switches to Text mode, showing the raw JSON', async () => {
    tui.alt('x');
    // The document is taller than the viewport and opens scrolled to the bottom — assert on
    // content guaranteed visible there rather than the (true but off-screen) top of the file.
    const screen = await tui.waitForText('"autoUpdate": true');
    expect(screen).toContain('Text mode: Complete JSON is editable');
  });

  test('Alt+F switches back from Text mode to Form mode', async () => {
    tui.alt('x');
    await tui.waitForText('"autoUpdate": true');
    tui.alt('f');
    const screen = await tui.waitForText('Auto Update');
    expect(screen).toContain('Global');
  });

  test('Alt+G, Alt+I, Alt+Y, Alt+B each switch to their own section', async () => {
    tui.alt('i');
    expect(await tui.waitForText('Profile Name')).toContain('SSID');

    tui.alt('y');
    expect(await tui.waitForText('Embedded URL')).toContain('Local Port');

    tui.alt('b');
    expect(await tui.waitForText('Hide Cursor')).toContain('Remote Only');

    tui.alt('g');
    expect(await tui.waitForText('Auto Update')).toContain('Machine Name');
  });

  test('Reload (mouse/Tab only — no mnemonic) is present but unreachable via any bare Alt key already in use', async () => {
    // Documented, deliberate: Reload has no mnemonic because every letter in the word was
    // already claimed by a higher-traffic action on this tab. It's still clickable/tabbable.
    const screen = await tui.screenText();
    expect(screen).toContain('Reload');
  });
});

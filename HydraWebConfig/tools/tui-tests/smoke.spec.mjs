import { test, expect } from '@playwright/test';
import { HydraTui } from './harness.mjs';

test('smoke: draws the Overview tab with connected status', async () => {
  const tui = await HydraTui.launch();
  try {
    const screen = await tui.waitForText('Connected');
    expect(screen).toContain('Hydra Control Center (Demo)');
    expect(screen).toContain('Overview');
  } finally {
    await tui.close();
  }
});

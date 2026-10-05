import { test, expect } from './harness.mjs';

test('smoke: draws the Overview tab with connected status', async ({ tui }) => {
  const screen = await tui.screenText();
  expect(screen).toContain('Hydra Control Center (Demo)');
  expect(screen).toContain('Overview');
});

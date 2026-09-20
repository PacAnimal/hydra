import { test, expect } from '@playwright/test';
import { HydraTui } from './harness.mjs';

test.describe('quitting and other cross-cutting behavior', () => {
  test('Esc quits the TUI process (Hydra itself keeps running is a separate concern)', async () => {
    const tui = await HydraTui.launch();
    try {
      await tui.waitForText('Connected');
      tui.key('escape');
      await tui.waitForExit(3000);
    } finally {
      await tui.close();
    }
  });

  test('F5 refresh keeps the Overview tab intact and connected', async () => {
    const tui = await HydraTui.launch();
    try {
      await tui.waitForText('Connected');
      tui.key('f5');
      await new Promise((r) => setTimeout(r, 300));
      const screen = await tui.screenText();
      expect(screen).toContain('Connected');
      expect(screen).toContain('Connection');
    } finally {
      await tui.close();
    }
  });

  test('--color tints the connection line differently than the default', async () => {
    const plain = await HydraTui.launch({ color: false });
    const colored = await HydraTui.launch({ color: true });
    try {
      await plain.waitForText('Connected');
      await colored.waitForText('Connected');
      // Row 1 is the connection status line; column 0 is the window border, column 1 a space,
      // column 2 the "●" status dot itself.
      const plainColor = await plain.cellColor(1, 2);
      const coloredColor = await colored.cellColor(1, 2);
      expect(coloredColor).not.toBeNull();
      expect(coloredColor).not.toBe(plainColor);
    } finally {
      await plain.close();
      await colored.close();
    }
  });
});

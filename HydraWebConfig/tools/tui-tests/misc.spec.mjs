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

  // The connection line is a LAMP plus FACTS, in two colours, unconditionally. Every other colour in the
  // TUI is unconditional too, so gating the one that carries information would make it the only one
  // anybody had to ask for.
  test('the connection lamp is tinted by state and the detail beside it is not', async () => {
    const tui = await HydraTui.launch();
    try {
      await tui.waitForText('Connected');
      // Row 1 is the connection line; column 0 is the window border, column 1 a space, column 2 the
      // "●" lamp. The detail starts after "● Connected", so column 20 is safely inside it.
      const lamp = await tui.cellColor(1, 2);
      const detail = await tui.cellColor(1, 20);

      expect(lamp).not.toBeNull();
      expect(detail).not.toBeNull();
      expect(lamp).not.toBe(detail);

      // The lamp says CONNECTED here, so it must be the green one. Without this the test passes with a
      // red lamp on a healthy connection — it would only be checking that two cells differ, which the
      // old --color comparison at least had a control for.
      //
      // cellColor hands back xterm.js's getFgColor(), which is a NUMBER (0xRRGGBB), not a string.
      const r = (lamp >> 16) & 0xff;
      const g = (lamp >> 8) & 0xff;
      const b = lamp & 0xff;
      const hex = `#${lamp.toString(16).padStart(6, '0')}`;
      expect(g, `lamp should be green when connected, got ${hex}`).toBeGreaterThan(r + 40);
      expect(g, `lamp should be green when connected, got ${hex}`).toBeGreaterThan(b + 40);
    } finally {
      await tui.close();
    }
  });
});

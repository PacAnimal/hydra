import { test, expect } from '@playwright/test';
import { HydraTui } from './harness.mjs';

test.describe('tab navigation', () => {
  let tui;

  test.beforeEach(async () => {
    tui = await HydraTui.launch();
    await tui.waitForText('Connected');
  });

  test.afterEach(async () => {
    await tui.close();
  });

  test('starts on Overview with real status data', async () => {
    const screen = await tui.screenText();
    expect(screen).toContain('Connection');
    expect(screen).toContain('Active Network Links');
    expect(screen).toContain('Embedded Relay Clients');
    expect(screen).toContain('Peer Latency');
    expect(screen).toContain('Routing');
    expect(screen).toContain('relay.example.com');
  });

  test('Alt+P switches to Peers & Screens', async () => {
    tui.alt('p');
    const screen = await tui.waitForText('Local Screens');
    expect(screen).toContain('Peers');
    expect(screen).toContain('laptop');
    expect(screen).toContain('[MacOS]');
  });

  test('Alt+L switches to Logs and shows real log entries', async () => {
    tui.alt('l');
    const screen = await tui.waitForText('Connected to Styx relay');
    expect(screen).toContain('Information');
    expect(screen).toContain('Relay.RelayConnection');
  });

  test('Alt+C switches to Configuration, defaulting to the Global section', async () => {
    tui.alt('c');
    const screen = await tui.waitForText('Machine Name');
    expect(screen).toContain('Global');
    expect(screen).toContain('desktop'); // the demo machine name
  });

  test('Alt+R switches to Remote', async () => {
    tui.alt('r');
    const screen = await tui.waitForText('Peer host');
    expect(screen).toContain('Pairing code');
    expect(screen).toContain('Pair');
  });

  test('Alt+D switches to Diagnostics', async () => {
    tui.alt('d');
    const screen = await tui.waitForText('Management');
    expect(screen).toContain('Protocol');
    expect(screen).toContain('connected');
  });

  test('Alt+H switches to Help', async () => {
    tui.alt('h');
    const screen = await tui.waitForText('Move between controls');
    expect(screen).toContain('Quit the TUI');
  });

  test('F1 jumps to Help from any tab', async () => {
    tui.alt('c');
    await tui.waitForText('Machine Name');
    tui.key('f1');
    const screen = await tui.waitForText('Move between controls');
    expect(screen).toContain('Quit the TUI');
  });

  test('can navigate through every tab in sequence and back to Overview', async () => {
    for (const [letter, marker] of [
      ['p', 'Local Screens'],
      ['l', 'Relay.RelayConnection'],
      ['c', 'Machine Name'],
      ['r', 'Pairing code'],
      ['d', 'Protocol'],
      ['h', 'Move between controls'],
      ['o', 'Active Network Links'],
    ]) {
      tui.alt(letter);
      const screen = await tui.waitForText(marker);
      expect(screen).toContain(marker);
    }
  });
});

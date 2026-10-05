import { test, expect } from './harness.mjs';

test.describe('tab navigation', () => {
  test('starts on Overview with real status data', async ({ tui }) => {
    const screen = await tui.screenText();
    expect(screen).toContain('Connection');
    expect(screen).toContain('Active Network Links');
    expect(screen).toContain('Embedded Relay Clients');
    expect(screen).toContain('Peer Latency');
    expect(screen).toContain('Routing');
    expect(screen).toContain('relay.example.com');
  });

  test('Alt+P switches to Peers & Screens', async ({ tui }) => {
    const screen = await tui.gotoTab('p', 'Local Screens');
    expect(screen).toContain('Peers');
    expect(screen).toContain('laptop');
    expect(screen).toContain('[macOS]');
  });

  test('Alt+L switches to Logs and shows real log entries', async ({ tui }) => {
    const screen = await tui.gotoTab('l', 'Connected to Styx relay');
    expect(screen).toContain('Information');
    expect(screen).toContain('Relay.RelayConnection');
  });

  test('Alt+C switches to Configuration, defaulting to the Global section', async ({ tui }) => {
    const screen = await tui.gotoTab('c', 'Machine Name');
    expect(screen).toContain('Global');
    expect(screen).toContain('desktop'); // the demo machine name
  });

  test('Alt+R switches to Remote', async ({ tui }) => {
    const screen = await tui.gotoTab('r', 'Peer host');
    expect(screen).toContain('Pairing code');
    expect(screen).toContain('Pair');
  });

  test('Alt+D switches to Diagnostics', async ({ tui }) => {
    const screen = await tui.gotoTab('d', 'Management');
    expect(screen).toContain('Protocol');
    expect(screen).toContain('connected');
  });

  test('Alt+H switches to Help', async ({ tui }) => {
    const screen = await tui.gotoTab('h', 'Move between controls');
    expect(screen).toContain('Quit the TUI');
  });

  test('F1 jumps to Help from any tab', async ({ tui }) => {
    await tui.gotoTab('c', 'Machine Name');
    tui.key('f1');
    const screen = await tui.waitForText('Move between controls');
    expect(screen).toContain('Quit the TUI');
  });

  test('can navigate through every tab in sequence and back to Overview', async ({ tui }) => {
    for (const [letter, marker] of [
      ['p', 'Local Screens'],
      ['l', 'Relay.RelayConnection'],
      ['c', 'Machine Name'],
      ['r', 'Pairing code'],
      ['d', 'Protocol'],
      ['h', 'Move between controls'],
      ['o', 'Active Network Links'],
    ]) {
      const screen = await tui.gotoTab(letter, marker);
      expect(screen).toContain(marker);
    }
  });
});

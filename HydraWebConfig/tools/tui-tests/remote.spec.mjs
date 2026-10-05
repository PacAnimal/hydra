import { test, expect } from './harness.mjs';

test.describe('Remote tab', () => {
  test.beforeEach(async ({ tui }) => {
    await tui.gotoTab('r', 'Pairing code');
  });

  test('shows the peer host and pairing code fields with all four actions', async ({ tui }) => {
    const screen = await tui.screenText();
    expect(screen).toContain('Peer host');
    expect(screen).toContain('Pairing code');
    expect(screen).toContain('Pair');
    expect(screen).toContain('Load Config');
    expect(screen).toContain('Validate');
    expect(screen).toContain('Save && Apply');
  });

  test('starts with placeholder guidance text before any peer is selected', async ({ tui }) => {
    const screen = await tui.screenText();
    expect(screen).toContain('Select a peer, pair it locally, then load its redacted');
  });
});

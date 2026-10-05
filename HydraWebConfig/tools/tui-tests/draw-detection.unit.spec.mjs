import { test, expect } from '@playwright/test';
import { drawnFrameEnd, recordPty, waitForDraw } from '../tui-screenshot/pty-helpers.mjs';

const TITLE = '\x1b]0;Hydra Control Center (Demo)\x1b\\';
const DRAWN = '\x1b[1;1H╭┤Hydra Control Center (Demo)├╮';
const FRAME_END = '\x1b[?25l';

// a node-pty stand-in that the test feeds by hand
function fakePty() {
  const data = [];
  const exit = [];
  return {
    onData: (cb) => data.push(cb),
    onExit: (cb) => exit.push(cb),
    write: (s) => data.forEach((cb) => cb(s)),
    exit: () => exit.forEach((cb) => cb({ exitCode: 1 })),
  };
}

test.describe('draw detection', () => {
  test('the window title alone is not drawn text', () => {
    expect(drawnFrameEnd(TITLE + FRAME_END, 'Hydra Control Center')).toBe(-1);
  });

  test('drawn text counts only once its frame has ended', () => {
    expect(drawnFrameEnd(TITLE + DRAWN, 'Hydra Control Center')).toBe(-1);
    const raw = TITLE + FRAME_END + DRAWN + FRAME_END + 'next frame';
    expect(drawnFrameEnd(raw, 'Hydra Control Center')).toBe(raw.indexOf('next frame'));
  });

  test('a frame before `from` does not count', () => {
    const raw = DRAWN + FRAME_END;
    expect(drawnFrameEnd(raw, 'Hydra Control Center', raw.length)).toBe(-1);
  });

  test('waitForDraw rejects when the process exits having only set its title', async () => {
    const term = fakePty();
    const recording = recordPty(term);
    const wait = waitForDraw(recording, 'Hydra Control Center', { timeoutMs: 60_000 });
    term.write(TITLE + FRAME_END);
    term.exit();
    await expect(wait).rejects.toThrow('exited before drawing');
  });

  test('waitForDraw resolves at the end of the frame that drew the text', async () => {
    const term = fakePty();
    const recording = recordPty(term);
    const wait = waitForDraw(recording, 'Hydra Control Center', { timeoutMs: 60_000 });
    term.write(TITLE + DRAWN);
    term.write(FRAME_END + 'next');
    expect(await wait).toBe(recording.output.indexOf('next'));
  });

  test('waitForDraw rejects at its deadline', async () => {
    const recording = recordPty(fakePty());
    await expect(waitForDraw(recording, 'Hydra Control Center', { timeoutMs: 10 })).rejects.toThrow('took over 10 ms');
  });
});

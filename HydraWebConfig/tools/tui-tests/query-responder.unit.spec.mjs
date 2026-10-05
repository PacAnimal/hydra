import { test, expect } from '@playwright/test';
import { queryResponder, replyTo } from '../tui-screenshot/pty-helpers.mjs';

test.describe('terminal query answering', () => {
  const replies = {
    '\x1b[6n': '\x1b[1;1R',
    '\x1b[18t': '\x1b[8;42;130t',
    '\x1b]10;?\x1b\\': '\x1b]10;rgb:e0e0/e0e0/e0e0\x1b\\',
    '\x1b]11;?\x1b\\': '\x1b]11;rgb:1e1e/1e1e/1e1e\x1b\\',
    '\x1b[?u': '\x1b[?0u',
    '\x1b[0c': '\x1b[?62;1;2;6;9;15;18;21;22c',
  };

  for (const [query, reply] of Object.entries(replies)) {
    test(`answers ${JSON.stringify(query)}`, () => {
      expect(queryResponder(130, 42)(`before${query}after`)).toBe(reply);
    });
  }

  test('refuses a query it has no reply for', () => {
    expect(() => replyTo('\x1b[5n', 130, 42)).toThrow('no reply for terminal query');
  });

  test('answers a query split across reads, once', () => {
    const respond = queryResponder(130, 42);
    expect(respond('\x1b[?1049h\x1b[1')).toBe('');
    expect(respond('8t')).toBe('\x1b[8;42;130t');
    expect(respond('more output')).toBe('');
  });

  test('answers every query in a read, in order', () => {
    const respond = queryResponder(130, 42);
    expect(respond('\x1b[6n\x1b]11;?\x1b\\')).toBe('\x1b[1;1R\x1b]11;rgb:1e1e/1e1e/1e1e\x1b\\');
  });
});

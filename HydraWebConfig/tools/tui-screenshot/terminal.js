// The one terminal render.html and tui-tests/live.html both draw into, so a screenshot and a test see
// the same font, size and colours.
window.createHydraTerminal = (cols, rows) =>
  new Terminal({
    cols,
    rows,
    fontFamily: 'Menlo, "SF Mono", Consolas, "Liberation Mono", monospace',
    fontSize: 13,
    lineHeight: 1.0,
    theme: { background: '#0c0c0c' },
    allowProposedApi: true,
  });

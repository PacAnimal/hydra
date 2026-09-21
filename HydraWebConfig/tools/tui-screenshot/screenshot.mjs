#!/usr/bin/env node
// Loads render.html (an xterm.js replay of capture.mjs's output) in a real headless
// browser and saves a PNG. Run `npm run capture` first to produce output/capture.b64.

import { createServer } from 'node:http';
import { readFile, writeFile } from 'node:fs/promises';
import { existsSync } from 'node:fs';
import { dirname, extname, join, resolve } from 'node:path';
import { fileURLToPath } from 'node:url';
import { chromium } from 'playwright';

const __dirname = dirname(fileURLToPath(import.meta.url));
const OUT_DIR = resolve(process.argv[2] ?? join(__dirname, 'output'));

const MIME = { '.html': 'text/html', '.json': 'application/json', '.b64': 'text/plain', '.js': 'text/javascript' };

async function serveOnce(rootDirs, port) {
  const server = createServer(async (req, res) => {
    const path = req.url === '/' ? '/render.html' : req.url;
    for (const dir of rootDirs) {
      const filePath = join(dir, path);
      if (existsSync(filePath)) {
        res.writeHead(200, { 'Content-Type': MIME[extname(filePath)] ?? 'application/octet-stream' });
        res.end(await readFile(filePath));
        return;
      }
    }
    res.writeHead(404);
    res.end('not found');
  });
  await new Promise((resolveListen) => server.listen(port, '127.0.0.1', resolveListen));
  return server;
}

/// Padding left around the content, in IMAGE pixels, on every side equally. The page renders at
/// deviceScaleFactor 2, so this is what a reader of the PNG sees, not a CSS length.
const PAD = 4;

/// The terminal's background, which is what "black" means here — xterm.js is themed #0c0c0c, and a
/// screenshot has no pure-black pixels to measure against.
const BG = [12, 12, 12];

/**
 * Trims a capture to its content and pads it evenly. Runs in the browser purely for its canvas: this
 * tool already carries a headless Chromium, and a PNG codec in Node would be a dependency bought for
 * one crop.
 *
 * The TEST that polices the result (Tests/Tui/ScreenshotPaddingTests) matches the background EXACTLY, with
 * no tolerance at all. The asymmetry is deliberate and safe in this direction only: a tolerance here can
 * only ever include MORE as content, so the border this leaves is painted by the fillRect below and is one
 * exact colour. Widening the test instead would let a near-black debug frame pass.
 *
 * "Content" is any pixel that differs from the background by more than a hair — the tolerance absorbs
 * the antialiasing xterm.js leaves around glyphs without letting a genuinely dark glyph edge be trimmed
 * off. A capture that is ENTIRELY background is returned untouched rather than collapsed to nothing.
 */
async function trimAndPad({ data, pad, bg }) {
  const image = new Image();
  image.src = `data:image/png;base64,${data}`;
  await image.decode();

  const source = document.createElement('canvas');
  source.width = image.naturalWidth;
  source.height = image.naturalHeight;
  const sourceCtx = source.getContext('2d', { willReadFrequently: true });
  sourceCtx.drawImage(image, 0, 0);

  const { data: px } = sourceCtx.getImageData(0, 0, source.width, source.height);
  const tolerance = 6;
  let top = source.height, left = source.width, right = -1, bottom = -1;

  for (let y = 0; y < source.height; y++) {
    for (let x = 0; x < source.width; x++) {
      const i = (y * source.width + x) * 4;
      if (Math.abs(px[i] - bg[0]) <= tolerance && Math.abs(px[i + 1] - bg[1]) <= tolerance && Math.abs(px[i + 2] - bg[2]) <= tolerance) continue;
      if (y < top) top = y;
      if (y > bottom) bottom = y;
      if (x < left) left = x;
      if (x > right) right = x;
    }
  }

  if (right < 0) return data; // nothing but background — hand it back rather than produce a 8x8 image

  const out = document.createElement('canvas');
  out.width = right - left + 1 + pad * 2;
  out.height = bottom - top + 1 + pad * 2;
  const outCtx = out.getContext('2d');
  outCtx.fillStyle = `rgb(${bg[0]}, ${bg[1]}, ${bg[2]})`;
  outCtx.fillRect(0, 0, out.width, out.height);
  outCtx.drawImage(source, left, top, right - left + 1, bottom - top + 1, pad, pad, right - left + 1, bottom - top + 1);

  return out.toDataURL('image/png').split(',')[1];
}

async function main() {
  if (!existsSync(join(OUT_DIR, 'capture.b64'))) {
    console.error(`No capture found at ${join(OUT_DIR, 'capture.b64')} — run "npm run capture" first.`);
    process.exit(1);
  }

  const port = 34567 + Math.floor(Math.random() * 1000);
  const server = await serveOnce([OUT_DIR, __dirname], port);
  try {
    const browser = await chromium.launch();
    try {
      // An explicit viewport, generous enough that #wrap always fits. Playwright clips an element
      // screenshot to the viewport, and the default 1280x720 leaves only ~64px of headroom at the
      // current 130x42 — so a taller capture would be silently truncated, and trim-and-pad would then
      // emit a perfectly padded TRUNCATED image that the padding test happily passes.
      const page = await browser.newPage({ deviceScaleFactor: 2, viewport: { width: 2400, height: 1800 } });
      await page.goto(`http://127.0.0.1:${port}/render.html`);
      await page.waitForFunction(() => window.__renderDone === true, { timeout: 10000 });
      await page.waitForTimeout(200); // let webfonts/layout settle
      const outPath = join(OUT_DIR, 'hydra-tui.png');

      // Capture #wrap, not #term: it is the terminal plus a margin of the terminal's OWN background, so
      // nothing foreign can be caught by a rounding error at the edges. The exact crop is then decided
      // from the PIXELS below rather than from an element's box, which is the part that used to be
      // fragile — an element crop is subject to subpixel layout, and two columns of the old debug frame
      // rode into the committed PNG down the left-hand side because of it.
      const shot = await page.locator('#wrap').screenshot();

      const trimmed = await page.evaluate(trimAndPad, { data: shot.toString('base64'), pad: PAD, bg: BG });
      await writeFile(outPath, Buffer.from(trimmed, 'base64'));
      console.error(`[done] wrote ${outPath}`);
    } finally {
      await browser.close();
    }
  } finally {
    server.close();
  }
}

main().catch((err) => {
  console.error(err);
  process.exit(1);
});

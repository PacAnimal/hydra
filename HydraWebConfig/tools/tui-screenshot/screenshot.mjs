#!/usr/bin/env node
// Loads render.html (an xterm.js replay of capture.mjs's output) in a real headless
// browser and saves a PNG. Run `npm run capture` first to produce output/capture.b64.

import { createServer } from 'node:http';
import { readFile } from 'node:fs/promises';
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
      const page = await browser.newPage({ deviceScaleFactor: 2 });
      await page.goto(`http://127.0.0.1:${port}/render.html`);
      await page.waitForFunction(() => window.__renderDone === true, { timeout: 10000 });
      await page.waitForTimeout(200); // let webfonts/layout settle
      const outPath = join(OUT_DIR, 'hydra-tui.png');
      // Screenshot only the terminal element, not the full page — #wrap's steel-blue frame in
      // render.html is a deliberately loud debug marker and must never end up in the saved PNG.
      await page.locator('#term').screenshot({ path: outPath });
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

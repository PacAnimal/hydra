import { defineConfig } from '@playwright/test';

export default defineConfig({
  testDir: '.',
  testMatch: '*.spec.mjs',
  timeout: 20_000,
  // Each test spawns its own hydra process, pty, and browser page — parallelism just adds
  // resource contention (and pty/CPU-scheduling flakiness) without a runtime benefit here.
  fullyParallel: false,
  workers: 1,
  retries: 0,
  reporter: [['list']],
  use: {
    headless: true,
  },
});

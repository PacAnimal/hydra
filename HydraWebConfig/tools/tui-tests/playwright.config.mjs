import { defineConfig } from '@playwright/test';

export default defineConfig({
  testDir: '.',
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
  projects: [
    // pure functions, no binary needed: `npx playwright test --project unit`
    { name: 'unit', testMatch: '*.unit.spec.mjs' },
    { name: 'warm-up', testMatch: 'warm-up.setup.mjs' },
    { name: 'tui', testMatch: '*.spec.mjs', testIgnore: '*.unit.spec.mjs', dependencies: ['warm-up'] },
  ],
});

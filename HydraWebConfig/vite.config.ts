import { configDefaults, defineConfig } from 'vitest/config'
import react from '@vitejs/plugin-react'

export default defineConfig({
  plugins: [react()],
  test: {
    environment: 'jsdom',
    setupFiles: ['./src/test-setup.ts'],
    globals: true,
    // tools/tui-tests is a standalone Playwright suite with its own package.json/node_modules —
    // it manages @playwright/test itself and runs via its own `npm test`, not Vitest.
    exclude: [...configDefaults.exclude, 'tools/tui-tests/**'],
  },
})

import { defineConfig, devices } from '@playwright/test'

const baseURL = process.env.E2E_BASE_URL ?? 'http://localhost:7331'
const browser = { ...devices['Desktop Chrome'], viewport: { width: 1440, height: 900 } }

/**
 * The browser tests (docs/05 §5), run by tools/e2e.sh in the Playwright image against a stack of their own. E2E_BASE_URL
 * is the app as the browser container reaches it: localhost, in the app container's network, so the page is a secure
 * context as with HTTPS in production – a service worker exists only there.
 */
export default defineConfig({
  testDir: 'e2e',
  outputDir: 'test-results/output',
  fullyParallel: false,
  workers: 1,
  retries: 0,
  reporter: [['list']],
  timeout: 60_000,
  expect: { timeout: 10_000 },
  use: {
    baseURL,
    trace: 'retain-on-failure',
    screenshot: 'only-on-failure',
  },
  // The first admin's test needs the fresh stack (the initial password, changed there): it runs before every other file,
  // whatever their names.
  projects: [
    { name: 'first-admin', testMatch: /identity\.spec\.ts$/, use: browser },
    { name: 'chromium', testIgnore: /identity\.spec\.ts$/, dependencies: ['first-admin'], use: browser },
  ],
})

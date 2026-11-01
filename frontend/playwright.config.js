import { defineConfig } from '@playwright/test';

export default defineConfig({
  testDir: './tests/browser',
  fullyParallel: true,
  workers: 2,
  retries: 0,
  reporter: 'list',
  globalSetup: './tests/browser/server.mjs',
  use: {
    baseURL: `http://localhost:${process.env.CODERANK_TEST_PORT || 5173}`,
    channel: process.env.PLAYWRIGHT_CHANNEL || (process.platform === 'win32' ? 'chrome' : undefined),
    screenshot: 'only-on-failure',
    trace: 'off', // Do not record credentials or response tokens, even when testing with fixtures.
    serviceWorkers: 'block',
  },
  projects: [
    { name: 'desktop', use: { viewport: { width: 1440, height: 1000 } } },
    { name: 'mobile', use: { viewport: { width: 390, height: 844 }, isMobile: true, hasTouch: true } },
  ],
});

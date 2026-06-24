import { defineConfig, devices } from '@playwright/test';

/**
 * Playwright configuration for the FreshCart customer e2e suite.
 *
 * The base URL is supplied by the PLAYWRIGHT_BASE_URL environment variable so the same suite
 * can be aimed at local Aspire (`http://localhost:4200`), the dev environment, or staging.
 */
const baseUrl = process.env['PLAYWRIGHT_BASE_URL'] ?? 'http://localhost:4200';
const isCi = !!process.env['CI'];

export default defineConfig({
  testDir: './specs',
  outputDir: './test-results',
  fullyParallel: true,
  forbidOnly: isCi,
  retries: isCi ? 2 : 0,
  workers: isCi ? 2 : undefined,
  // A full cold-stack journey (slow first sign-up, checkout saga, SignalR push) can run well past the
  // default 30s; 120s leaves headroom on a freshly booted, resource-contended environment.
  timeout: 120_000,
  expect: { timeout: 10_000 },

  reporter: [
    ['list'],
    ['html', { outputFolder: 'playwright-report', open: 'never' }],
    ['junit', { outputFile: 'test-results/junit.xml' }],
    ...(isCi ? [['github'] as const] : []),
  ],

  use: {
    baseURL: baseUrl,
    trace: 'retain-on-failure',
    screenshot: 'only-on-failure',
    video: 'retain-on-failure',
    actionTimeout: 15_000,
    navigationTimeout: 30_000,
    ignoreHTTPSErrors: true,
    testIdAttribute: 'data-testid',
  },

  projects: [
    { name: 'chromium', use: { ...devices['Desktop Chrome'] } },
    { name: 'firefox',  use: { ...devices['Desktop Firefox'] } },
    { name: 'webkit',   use: { ...devices['Desktop Safari'] } },
    { name: 'mobile-chromium', use: { ...devices['Pixel 7'] } },
  ],
});

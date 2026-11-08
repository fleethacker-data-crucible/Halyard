// Browser tests for the Halyard UI against a real engine and the fake GitHub (e2e/Start-UiHost.ps1).
//   npm run build && npm run e2e
// Uses the installed Edge (channel msedge), so nothing is downloaded. HALYARD_E2E_BROWSER_CHANNEL=chrome or '' changes it.
import { defineConfig } from '@playwright/test';
import path from 'node:path';
import { fileURLToPath } from 'node:url';

const here = path.dirname(fileURLToPath(import.meta.url));

const port = Number(process.env.HALYARD_E2E_PORT || 47700);
const TOKEN_FILE = path.join(here, '.output', 'session.txt');
const channel = process.env.HALYARD_E2E_BROWSER_CHANNEL ?? 'msedge';

export default defineConfig({
  testDir: here,
  testMatch: /.*\.spec\.ts$/,
  outputDir: path.join(here, '.output', 'results'),
  fullyParallel: false,
  workers: 1, // one shared engine
  retries: 0,
  timeout: 60_000,
  expect: { timeout: 10_000 },
  reporter: [['list']],
  use: { baseURL: `http://127.0.0.1:${port}`, viewport: { width: 1440, height: 900 }, trace: 'off' },
  projects: [{ name: 'edge', use: { browserName: 'chromium', ...(channel ? { channel } : {}) } }],
  webServer: {
    command: `powershell.exe -NoProfile -ExecutionPolicy Bypass -File "${path.join(here, 'Start-UiHost.ps1')}" -Port ${port} -FakePort ${port + 1} -TokenFile "${TOKEN_FILE}"`,
    url: `http://127.0.0.1:${port}/__fleethacker/id`,
    reuseExistingServer: false,
    timeout: 120_000,
    stdout: 'ignore',
  },
});

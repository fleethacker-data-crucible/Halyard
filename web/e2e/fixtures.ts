// Shared helpers: open Halyard the way the launcher does (the one-time ?session= link), and fail any test that produced a CSP
// violation, an uncaught page error or an unexpected console error.
import { test as base, expect, type Page } from '@playwright/test';
import fs from 'node:fs';
import path from 'node:path';
import { fileURLToPath } from 'node:url';

const here = path.dirname(fileURLToPath(import.meta.url));

const TOKEN_FILE = path.join(here, '.output', 'session.txt');
// Browsers log non-2xx fetches as console errors; some are expected (no model key, TalkForge not installed in the fixture).
const EXPECTED = /Failed to load resource: the server responded with a status of (400|404|409)/;

/** The session token, once Start-UiHost.ps1 has seeded and synchronized the backlog (it writes the file last). */
export async function sessionToken(): Promise<string> {
  const until = Date.now() + 90_000;
  while (Date.now() < until) {
    try { const t = fs.readFileSync(TOKEN_FILE, 'utf8').trim(); if (t) return t; } catch { /* not yet */ }
    await new Promise((r) => setTimeout(r, 500));
  }
  throw new Error('The UI host did not write its session token in time.');
}

export const test = base.extend<{ problems: string[]; open: (hash?: string, opts?: { welcome?: boolean }) => Promise<Page> }>({
  problems: [
    async ({ page }, use) => {
      const problems: string[] = [];
      page.on('pageerror', (e) => problems.push(`page error: ${e.message}`));
      page.on('console', (m) => {
        if (m.type() !== 'error') return;
        const t = m.text();
        if (EXPECTED.test(t)) return;
        problems.push(`${/Content Security Policy/i.test(t) ? 'CSP' : 'console'}: ${t}`);
      });
      await use(problems);
      expect(problems, 'no page errors, CSP violations or console errors').toEqual([]);
    },
    { auto: true },
  ],
  open: async ({ page }, use) => {
    await use(async (hash = '', opts = {}) => {
      const token = await sessionToken();
      // Skipping the welcome page is a per-browser preference; each test decides.
      await page.addInitScript((skip) => { try { localStorage.setItem('halyard.skip-welcome', JSON.stringify(skip)); } catch { /* blocked */ } }, !opts.welcome);
      await page.goto(`/?session=${token}${hash}`);
      return page;
    });
  },
});

export { expect };

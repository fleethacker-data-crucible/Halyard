// README screenshots of the Overview in both Fleet themes (Night watch = dark, Chart room = light), and the
// walkthrough images (docs/walkthrough/README.md, Night watch) taken along the same run, step by step.
// Mock data only: the backlog is the synthetic seed written to the in-memory fake GitHub
// (tests/fakes/fake-github.mjs). No token, no network, no real repository.
// Run it through tests/e2e/Capture-Screenshots.ps1, which starts the fake and a fresh engine (no snapshot yet:
// the first walkthrough image is the empty first launch).
//
//   node tests/e2e/screenshots.mjs <base-url> <session-token> <fake-url> <proxy-port> [out-dir] [walkthrough-dir]
//
// out-dir defaults to docs/screenshots, walkthrough-dir to docs/assets/walkthrough; pass "-" to skip either.
//
// The engine talks to the fake through a small proxy started here, which only renames the fake's
// signed-in login to a neutral demo name and lists only the invented example-org repositories for the picture. Needs Playwright next to the web UI:
//   cd web; npm install --no-save playwright; npx playwright install chromium
import { createServer, request as httpRequest } from 'node:http';
import { createRequire } from 'node:module';
import { mkdirSync } from 'node:fs';
import { dirname, join, resolve } from 'node:path';
import { fileURLToPath } from 'node:url';

const root = resolve(dirname(fileURLToPath(import.meta.url)), '..', '..');
const [base, token, fakeUrl, proxyPortArg, outArg, walkArg] = process.argv.slice(2);
if (!base || !token || !fakeUrl || !proxyPortArg) {
  console.error('usage: node tests/e2e/screenshots.mjs <base-url> <session-token> <fake-url> <proxy-port> [out-dir] [walkthrough-dir]');
  process.exit(2);
}
const out = outArg === '-' ? null : resolve(outArg ?? join(root, 'docs', 'screenshots'));
const walk = walkArg === '-' ? null : resolve(walkArg ?? join(root, 'docs', 'assets', 'walkthrough'));
const { chromium } = createRequire(join(root, 'web', 'package.json'))('playwright');

// Proxy to the fake GitHub: everything passes through; GET /user answers with a demo identity.
const fake = new URL(fakeUrl);
const proxy = createServer((req, res) => {
  if (req.method === 'GET' && req.url === '/user') {
    res.writeHead(200, { 'Content-Type': 'application/json' });
    return res.end(JSON.stringify({ login: 'demo-product-owner', name: 'Demo Product Owner', html_url: 'https://github.com/demo' }));
  }
  if (req.method === 'GET' && req.url.startsWith('/user/repos')) {
    // The fake always lists its built-in default sandbox too; keep the picture to the invented repositories.
    const up = httpRequest({ host: fake.hostname, port: fake.port, path: req.url, method: 'GET', headers: req.headers }, (r) => {
      let body = '';
      r.setEncoding('utf8');
      r.on('data', (c) => { body += c; });
      r.on('end', () => {
        let rows = null;
        try { rows = JSON.parse(body); } catch { /* pass through below */ }
        res.writeHead(r.statusCode ?? 502, { 'Content-Type': 'application/json' });
        res.end(Array.isArray(rows) ? JSON.stringify(rows.filter((x) => String(x.full_name).startsWith('example-org/'))) : body);
      });
    });
    up.on('error', () => { res.writeHead(502); res.end(); });
    return up.end();
  }
  const up = httpRequest({ host: fake.hostname, port: fake.port, path: req.url, method: req.method, headers: req.headers }, (r) => {
    res.writeHead(r.statusCode ?? 502, r.headers);
    r.pipe(res);
  });
  up.on('error', () => { res.writeHead(502); res.end(); });
  req.pipe(up);
});
await new Promise((ok) => proxy.listen(Number(proxyPortArg), '127.0.0.1', ok));

const H = { 'X-Halyard-Session': token, 'Content-Type': 'application/json' };
async function call(method, path, body) {
  const r = await fetch(base + path, { method, headers: H, body: body === undefined ? undefined : JSON.stringify(body) });
  const data = await r.json().catch(() => null);
  if (!r.ok) throw new Error(`${method} ${path} -> ${r.status} ${data?.code}: ${data?.error}`);
  return data;
}
async function waitJob(started, label) {
  const id = started.job?.id ?? started.id;
  for (let i = 0; i < 600; i++) {
    const j = await call('GET', `/api/jobs/${id}`);
    if (j.state === 'succeeded') return j.result;
    if (j.state === 'failed' || j.state === 'cancelled') throw new Error(`${label} ${j.state}: ${j.error}`);
    await new Promise((r) => setTimeout(r, 300));
  }
  throw new Error(`${label} timed out`);
}

const VIEWPORT = { width: 1440, height: 900 };
const settle = (page, ms = 800) => page.waitForTimeout(ms); // let charts and toasts settle

// Walkthrough page in the Night watch theme, opened once with the session link like the launcher does.
async function openWalkthrough(browser) {
  const context = await browser.newContext({ viewport: VIEWPORT, deviceScaleFactor: 1, colorScheme: 'dark' });
  const page = await context.newPage();
  await page.goto(`${base}/?session=${token}`);
  await page.getByRole('heading', { name: 'Overview' }).waitFor();
  return { context, page };
}
async function go(page, route, heading) {
  await page.evaluate((r) => { window.location.hash = '/' + r; }, route);
  await page.getByRole('heading', { name: heading, exact: true, level: 1 }).waitFor();
}
async function shot(page, name) {
  // Toasts are transient; wait them out so a step image shows the page, not the notification.
  await page.locator('[aria-live="polite"] > div').first().waitFor({ state: 'detached', timeout: 10000 }).catch(() => {});
  await settle(page);
  const file = join(walk, name);
  await page.screenshot({ path: file });
  console.log('wrote ' + file);
}

try {
  if (out) mkdirSync(out, { recursive: true });
  if (walk) mkdirSync(walk, { recursive: true });
  const browser = await chromium.launch();
  try {
    let w = null;
    if (walk) {
      w = await openWalkthrough(browser);
      const { page } = w;
      // 1. First launch: nothing synchronized yet.
      await page.getByText('Nothing to show yet').waitFor();
      await shot(page, '01-first-launch.png');
      // 2. Settings: the model key and name.
      await go(page, 'settings', 'Settings');
      await shot(page, '02-settings-model.png');
      // 3. Sandbox: seed 100 synthetic items, about 20 of them with gaps, from the page itself.
      await go(page, 'sandbox', 'Sandbox');
      await page.getByLabel('Total work items').fill('100');
      await page.getByLabel('Run id').fill('readme-demo');
      await page.getByRole('button', { name: /^Seed 100 items into the sandbox/ }).click();
      await page.getByText(/^Run readme-demo:/).waitFor({ timeout: 120000 });
      await page.getByRole('button', { name: 'Refresh' }).click();
      await page.getByRole('cell', { name: 'readme-demo' }).first().waitFor();
      await shot(page, '03-sandbox-seed.png');
      // 4. Connection: synchronize the sandbox (the page moves on to Overview when it is done), then come
      // back and test the token, so the step shows both the confirmed sign-in and the new snapshot.
      await go(page, 'connection', 'Connection');
      await page.getByLabel('Repository to read').selectOption('sandbox');
      await page.getByRole('button', { name: /^Synchronize / }).click();
      await page.waitForURL(/#\/overview/, { timeout: 120000 });
      await page.getByText('work items').first().waitFor();
      await settle(page);
      await go(page, 'connection', 'Connection');
      await page.getByRole('button', { name: 'Test connection' }).click();
      await page.getByText(/^Signed in as demo-product-owner; repository/).waitFor();
      await page.getByText('Labels in the current snapshot').waitFor();
      await shot(page, '04-connection-sync.png');
    } else {
      await call('POST', '/api/provider/test');
      // The README's quick start: 100 synthetic items, about 20 of them with gaps.
      await waitJob(await call('POST', '/api/sandbox/seed', { total: 100, runId: 'readme-demo', includeDefects: true }), 'seed');
      const sync = await waitJob(await call('POST', '/api/sync', { repository: 'sandbox' }), 'sync');
      console.log(`snapshot of ${sync.count} synthetic items`);
    }

    // README: the Overview of the synchronized sandbox in both themes.
    if (out) {
      for (const [scheme, theme] of [['dark', 'night-watch'], ['light', 'chart-room']]) {
        const context = await browser.newContext({ viewport: VIEWPORT, deviceScaleFactor: 1, colorScheme: scheme });
        const page = await context.newPage();
        await page.goto(`${base}/?session=${token}`);
        await page.getByRole('heading', { name: 'Overview' }).waitFor();
        await page.getByText('work items').first().waitFor();
        await settle(page);
        const file = join(out, `overview-${theme}.png`);
        await page.screenshot({ path: file });
        console.log('wrote ' + file);
        await context.close();
      }
    }

    if (w) {
      const { page } = w;
      // 5. Overview of the demo sandbox.
      await go(page, 'overview', 'Overview');
      await page.getByText('work items').first().waitFor();
      await shot(page, '05-overview.png');
      // 6. Review: select three items that need attention to show the bulk bar.
      await go(page, 'review', 'Review');
      const boxes = page.getByRole('checkbox', { name: /^Select #/ });
      await boxes.first().waitFor();
      for (let i = 0; i < 3; i++) await boxes.nth(i).check();
      await shot(page, '06-review-gaps.png');
      // 7. Proposals: repair metadata for the selection, open the first proposal's diff and approve it.
      await page.getByRole('button', { name: /^Repair metadata/ }).click();
      await page.waitForURL(/#\/proposals/, { timeout: 30000 }).catch(() => go(page, 'proposals', 'Proposals'));
      await page.getByRole('heading', { name: 'Proposals', exact: true, level: 1 }).waitFor();
      const approve = page.getByRole('button', { name: 'Approve', exact: true }).first();
      await approve.waitFor();
      await page.locator('tbody tr.cursor-pointer').first().click();
      await approve.click();
      await shot(page, '07-proposal-diff.png');
      await w.context.close();
    }
  } finally {
    await browser.close();
  }
} finally {
  proxy.close();
}

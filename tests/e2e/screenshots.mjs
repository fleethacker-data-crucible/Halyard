// README screenshots of the Overview in both Fleet themes (Night watch = dark, Chart room = light).
// Mock data only: the backlog is the synthetic seed (POST /api/sandbox/seed) written to the in-memory
// fake GitHub (tests/fakes/fake-github.mjs). No token, no network, no real repository.
// Run it through tests/e2e/Capture-Screenshots.ps1, which starts the fake and the engine.
//
//   node tests/e2e/screenshots.mjs <base-url> <session-token> <fake-url> <proxy-port> [out-dir]
//
// The engine talks to the fake through a small proxy started here, which only renames the fake's
// signed-in login to a neutral demo name for the picture. Needs Playwright next to the web UI:
//   cd web; npm install --no-save playwright; npx playwright install chromium
import { createServer, request as httpRequest } from 'node:http';
import { createRequire } from 'node:module';
import { mkdirSync } from 'node:fs';
import { dirname, join, resolve } from 'node:path';
import { fileURLToPath } from 'node:url';

const root = resolve(dirname(fileURLToPath(import.meta.url)), '..', '..');
const [base, token, fakeUrl, proxyPortArg, outArg] = process.argv.slice(2);
if (!base || !token || !fakeUrl || !proxyPortArg) {
  console.error('usage: node tests/e2e/screenshots.mjs <base-url> <session-token> <fake-url> <proxy-port> [out-dir]');
  process.exit(2);
}
const out = resolve(outArg ?? join(root, 'docs', 'screenshots'));
const { chromium } = createRequire(join(root, 'web', 'package.json'))('playwright');

// Proxy to the fake GitHub: everything passes through; GET /user answers with a demo identity.
const fake = new URL(fakeUrl);
const proxy = createServer((req, res) => {
  if (req.method === 'GET' && req.url === '/user') {
    res.writeHead(200, { 'Content-Type': 'application/json' });
    return res.end(JSON.stringify({ login: 'demo-product-owner', name: 'Demo Product Owner', html_url: 'https://github.com/demo' }));
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

try {
  await call('POST', '/api/provider/test');
  // The README's quick start: 100 synthetic items, about 20 of them with gaps.
  await waitJob(await call('POST', '/api/sandbox/seed', { total: 100, runId: 'readme-demo', includeDefects: true }), 'seed');
  const sync = await waitJob(await call('POST', '/api/sync', { repository: 'sandbox' }), 'sync');
  console.log(`snapshot of ${sync.count} synthetic items`);

  mkdirSync(out, { recursive: true });
  const browser = await chromium.launch();
  try {
    for (const [scheme, theme] of [['dark', 'night-watch'], ['light', 'chart-room']]) {
      const context = await browser.newContext({ viewport: { width: 1440, height: 900 }, deviceScaleFactor: 1, colorScheme: scheme });
      const page = await context.newPage();
      await page.goto(`${base}/?session=${token}`);
      await page.getByRole('heading', { name: 'Overview' }).waitFor();
      await page.getByText('work items').first().waitFor();
      await page.waitForTimeout(800); // let the charts settle
      const file = join(out, `overview-${theme}.png`);
      await page.screenshot({ path: file });
      console.log('wrote ' + file);
      await context.close();
    }
  } finally {
    await browser.close();
  }
} finally {
  proxy.close();
}

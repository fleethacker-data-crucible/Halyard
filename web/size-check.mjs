// Fails when the built UI outgrows size-budget.json: what the first paint needs, all JavaScript, and the CSS.
// Run after `npm run build` (CI does). Sizes come from dist/build.json, which build.mjs writes.
import { readFile } from 'node:fs/promises';

const budget = JSON.parse(await readFile(new URL('./size-budget.json', import.meta.url), 'utf8'));
const built = JSON.parse(await readFile(new URL('./dist/build.json', import.meta.url), 'utf8'));
const kb = (b) => Math.round(b / 1024);
const rows = [
  ['first load', kb(built.entryBytes), budget.firstLoadKb],
  ['all JavaScript', kb(built.totalBytes), budget.totalJsKb],
  ['CSS', kb(built.cssBytes), budget.cssKb],
];
let failed = false;
for (const [what, size, limit] of rows) {
  const ok = size <= limit;
  failed ||= !ok;
  console.log(`${ok ? 'ok  ' : 'OVER'} ${what}: ${size} KB of ${limit} KB`);
}
if (failed) { console.error('The UI is over its size budget (web/size-budget.json).'); process.exit(1); }

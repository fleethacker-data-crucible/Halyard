// Builds the Halyard UI to static files with esbuild and Tailwind's compiler API.
// Why not Vite: the runtime target is a locked-down workstation that only ever serves
// pre-built files from a local process, so the build is the whole story. Two tools,
// no plugins, no dev server to secure.
//
//   node build.mjs            production build to dist/
//   node build.mjs --watch    rebuild on change (point the host at web/dist with -WebRoot)

import { build } from 'esbuild';
import { compile } from 'tailwindcss';
import { createHash } from 'node:crypto';
import { cp, mkdir, readFile, readdir, rm, stat, writeFile } from 'node:fs/promises';
import { createRequire } from 'node:module';
import path from 'node:path';
import { fileURLToPath } from 'node:url';

const here = path.dirname(fileURLToPath(import.meta.url));
const srcDir = path.join(here, 'src');
const outDir = path.join(here, 'dist');
const publicDir = path.join(here, 'public');
const watch = process.argv.includes('--watch');
const require = createRequire(import.meta.url);

async function listFiles(dir) {
  const out = [];
  for (const entry of await readdir(dir, { withFileTypes: true })) {
    const full = path.join(dir, entry.name);
    if (entry.isDirectory()) out.push(...(await listFiles(full)));
    else out.push(full);
  }
  return out;
}

// Tailwind normally scans with a native module; here we extract class candidates ourselves.
// Any token that is not a utility is ignored by the compiler, so being generous is harmless.
async function extractCandidates(files) {
  const candidates = new Set();
  const tokenRe = /[^\s"'`<>{}=;]+/g;
  for (const file of files) {
    if (!/\.(tsx?|html|css)$/.test(file)) continue;
    const text = await readFile(file, 'utf8');
    for (const match of text.match(tokenRe) ?? []) {
      if (match.length < 2 || match.length > 80) continue;
      candidates.add(match);
      // Tokens glued to JS punctuation (e.g. cn('a', or ('a') need trimming to become class names.
      const trimmed = match.replace(/^[(,]+|[),]+$/g, '');
      if (trimmed && trimmed !== match) candidates.add(trimmed);
    }
  }
  return [...candidates];
}

async function buildCss(files) {
  const entry = await readFile(path.join(srcDir, 'styles.css'), 'utf8');
  const twRoot = path.dirname(require.resolve('tailwindcss/package.json'));
  const compiled = await compile(entry, {
    base: srcDir,
    async loadStylesheet(id, base) {
      if (id === 'tailwindcss') return { base: twRoot, content: await readFile(path.join(twRoot, 'index.css'), 'utf8') };
      if (id.startsWith('tailwindcss/')) {
        const file = path.join(twRoot, id.slice('tailwindcss/'.length));
        return { base: twRoot, content: await readFile(file, 'utf8') };
      }
      const file = path.resolve(base, id);
      return { base: path.dirname(file), content: await readFile(file, 'utf8') };
    },
    async loadModule() {
      throw new Error('JavaScript plugins are not used in this build.');
    },
  });
  return compiled.build(await extractCandidates(files));
}

async function run() {
  await rm(outDir, { recursive: true, force: true });
  await mkdir(path.join(outDir, 'assets'), { recursive: true });
  const files = await listFiles(srcDir);

  // ES modules with code splitting (as Semaphore builds): the shell loads first and each page arrives as its own chunk
  // when it is opened (React.lazy in app.tsx). Names carry a content hash, so a cached chunk is never stale.
  const result = await build({
    entryPoints: [path.join(srcDir, 'main.tsx')],
    bundle: true,
    splitting: true,
    minify: !watch,
    sourcemap: watch ? 'inline' : false,
    format: 'esm',
    target: ['es2020', 'edge120', 'chrome120'],
    jsx: 'automatic',
    define: { 'process.env.NODE_ENV': JSON.stringify(watch ? 'development' : 'production') },
    outdir: path.join(outDir, 'assets'),
    entryNames: 'app.[hash]',
    chunkNames: 'chunk.[hash]',
    metafile: true,
    write: true,
    legalComments: 'none',
    logLevel: 'warning',
  });
  const outputs = Object.entries(result.metafile.outputs).filter(([f]) => f.endsWith('.js'));
  const entry = outputs.find(([, o]) => o.entryPoint && /main\.tsx$/.test(o.entryPoint))?.[0];
  if (!entry) throw new Error('esbuild produced no entry file');
  const entryName = path.basename(entry);
  const js = await readFile(path.join(outDir, 'assets', entryName), 'utf8');
  // The speech-to-text worker is its own module file: it imports TalkForge's transformers.js from /stt/ at run time.
  await build({
    entryPoints: [path.join(srcDir, 'workers', 'stt.worker.ts')],
    bundle: true,
    minify: !watch,
    format: 'esm',
    target: ['es2020', 'edge120', 'chrome120'],
    outfile: path.join(outDir, 'assets', 'stt.worker.js'),
    legalComments: 'none',
    logLevel: 'warning',
  });
  const css = await buildCss(files);
  const hash = createHash('sha256').update(js).update(css).digest('hex').slice(0, 10);
  await writeFile(path.join(outDir, 'assets', `app.${hash}.css`), css);
  const html = (await readFile(path.join(here, 'index.html'), 'utf8'))
    .replace('%APP_JS%', `assets/${entryName}`)
    .replace('%APP_CSS%', `assets/app.${hash}.css`);
  await writeFile(path.join(outDir, 'index.html'), html);
  // Static files (favicon, emblem art) are copied as they are, at the same relative paths.
  await cp(publicDir, outDir, { recursive: true });
  // Sizes for the budget check (size-check.mjs): the entry the shell needs first, and every chunk.
  // What the first paint needs: the entry and the chunks it imports statically, followed transitively.
  const first = new Set();
  const visit = (f) => { if (first.has(f)) return; first.add(f); for (const i of result.metafile.outputs[f]?.imports ?? []) if (i.kind === 'import-statement') visit(i.path); };
  visit(entry);
  const entryBytes = (await Promise.all([...first].map(async (f) => (await stat(path.join(outDir, 'assets', path.basename(f)))).size))).reduce((a, b) => a + b, 0);
  const chunks = await Promise.all(outputs.map(async ([f]) => ({ file: path.basename(f), bytes: (await stat(path.join(outDir, 'assets', path.basename(f)))).size })));
  const totalBytes = chunks.reduce((n, c) => n + c.bytes, 0);
  await writeFile(path.join(outDir, 'build.json'), JSON.stringify({ hash, entry: entryName, entryBytes, totalBytes, cssBytes: css.length, chunks: chunks.length, builtAt: new Date().toISOString(), watch }, null, 2));
  console.log(`built dist/ (${entryName}: ${(entryBytes / 1024).toFixed(0)} KB on first load, ${chunks.length} files ${(totalBytes / 1024).toFixed(0)} KB in all, css ${(css.length / 1024).toFixed(0)} KB)`);
}

await run();

if (watch) {
  const { watch: fsWatch } = await import('node:fs');
  let timer = null;
  const schedule = () => {
    clearTimeout(timer);
    timer = setTimeout(() => run().catch((e) => console.error(e.message)), 150);
  };
  fsWatch(srcDir, { recursive: true }, schedule);
  fsWatch(path.join(here, 'index.html'), schedule);
  console.log('watching src/ for changes…');
}

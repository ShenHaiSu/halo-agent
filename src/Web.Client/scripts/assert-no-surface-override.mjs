// Production build guard: development surface overrides must never ship.
// Mirrors scripts/assert-no-mock.mjs: walks text products under wwwroot and
// fails the build when a development-only switch leaks into the bundle.
import { existsSync, readFileSync, readdirSync, statSync } from 'node:fs';
import { dirname, join, resolve } from 'node:path';
import { fileURLToPath } from 'node:url';

const here = dirname(fileURLToPath(import.meta.url));
const wwwroot = resolve(here, '../../Host.Desktop/wwwroot');
const NEEDLES = ['halo-surface-modes'];
const QUERY_HINT = /[?&]surface=/;
const TEXT_EXTS = new Set(['.js', '.mjs', '.cjs', '.html', '.css', '.json', '.txt', '.svg']);

function walk(dir, out) {
  for (const name of readdirSync(dir)) {
    const p = join(dir, name);
    const st = statSync(p);
    if (st.isDirectory()) walk(p, out);
    else out.push(p);
  }
  return out;
}

if (!existsSync(wwwroot)) {
  console.error(`[assert-no-surface-override] FAIL: wwwroot missing: ${wwwroot}`);
  process.exit(1);
}

const hits = [];
for (const file of walk(wwwroot, [])) {
  const lower = file.toLowerCase();
  const dot = lower.lastIndexOf('.');
  if (dot < 0 || !TEXT_EXTS.has(lower.slice(dot))) continue;
  let text = '';
  try {
    text = readFileSync(file, 'utf8');
  } catch {
    continue;
  }
  if (NEEDLES.some((needle) => text.includes(needle)) || QUERY_HINT.test(text)) {
    hits.push(file);
  }
}

if (hits.length > 0) {
  console.error('[assert-no-surface-override] FAIL: development surface override leaked into production bundle:');
  for (const h of hits.slice(0, 20)) console.error(`  - ${h}`);
  process.exit(1);
}

console.log('[assert-no-surface-override] OK: no surface override in wwwroot');

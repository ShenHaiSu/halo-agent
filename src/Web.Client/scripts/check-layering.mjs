// src/Web.Client/scripts/check-layering.mjs
/**
 * 检查分层边界（见 01-前置检查 §10.2）。
 *
 * ============ 为什么需要这个 ============
 *  分层是"口头约定"时，半年后一定会被破坏：
 *  某人在 HButton 里为了拿主题而 import 了 useThemeStore，
 *  然后画廊里就渲染不了按钮了。
 *  只有工具能守住这条线。
 * ============ ============
 */
import { readdir, readFile } from 'node:fs/promises';
import { join, relative } from 'node:path';
import { fileURLToPath } from 'node:url';

const SRC = join(fileURLToPath(new URL('../', import.meta.url)), 'src');

const RULES = [
  { dir: 'components/base', forbid: ['stores/', 'bridge/', 'transport/', '@/components/layout'] },
  { dir: 'components/icons', forbid: ['stores/', 'bridge/', 'transport/'] },
  { dir: 'theme', forbid: ['components/', 'stores/', 'bridge/'] },
  { dir: 'utils', forbid: ['components/', 'stores/', 'bridge/', 'transport/'] },
  { dir: 'composables', forbid: ['components/', 'stores/', 'transport/'] },
];

let errors = 0;

async function walk(dir) {
  const out = [];
  for (const e of await readdir(dir, { withFileTypes: true })) {
    const p = join(dir, e.name);
    if (e.isDirectory()) out.push(...(await walk(p)));
    else if (/\.(ts|vue)$/.test(e.name)) out.push(p);
  }
  return out;
}

const IMPORT_RE = /from\s+['"]([^'"]+)['"]/g;

for (const rule of RULES) {
  const dir = join(SRC, rule.dir);
  let files;
  try {
    files = await walk(dir);
  } catch {
    continue;
  }
  for (const f of files) {
    const rel = relative(SRC, f).replace(/\\/g, '/');
    const text = await readFile(f, 'utf8');
    IMPORT_RE.lastIndex = 0;
    let m;
    while ((m = IMPORT_RE.exec(text)) !== null) {
      const spec = m[1];
      for (const forbid of rule.forbid) {
        const blocked = forbid.startsWith('@/')
          ? spec.startsWith(forbid)
          : spec.includes(forbid) || spec.startsWith('@/' + forbid);
        if (blocked) {
          const line = text.slice(0, m.index).split('\n').length;
          console.error(`ERR  src/${rel}:${line}  ${rule.dir} 不得 import「${spec}」`);
          errors++;
        }
      }
    }
  }
}

if (errors > 0) {
  console.error(`\n✗ 发现 ${errors} 处分层违规（见 01-前置检查 §10.2）`);
  process.exit(1);
}
console.log('✓ 分层边界正确');

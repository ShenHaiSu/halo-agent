// src/Web.Client/scripts/check-import-order.mjs
/**
 * 校验 styles/index.css 的 @import 顺序。
 *
 * ============ 为什么顺序不能错 ============
 *  ① tokens 必须在 base 之前（reset 里可能引用变量）
 *  ② Tailwind 的三个 @tailwind 必须紧跟 tokens 之后、base 之前
 *     否则 utilities 会被 base 覆盖（例如 reset 的 * { margin: 0 } 压掉 p-4）
 *  ③ utilities.css 必须最后（它是最高优先级的微调层）
 * ============ ============
 */
import { readFile } from 'node:fs/promises';
import { join } from 'node:path';
import { fileURLToPath } from 'node:url';

const ROOT = fileURLToPath(new URL('../', import.meta.url));
const text = await readFile(join(ROOT, 'src/styles/index.css'), 'utf8');

const lines = text.split('\n');
const imports = [];
lines.forEach((l, i) => {
  const m = /@import\s+['"]([^'"]+)['"]/.exec(l);
  if (m) imports.push({ spec: m[1], line: i + 1 });
  const t = /@tailwind\s+(\w+)/.exec(l);
  if (t) imports.push({ spec: `@tailwind ${t[1]}`, line: i + 1 });
});

/** 期望的先后顺序（按类别） */
const ORDER = [
  'tokens/',
  '@tailwind base',
  'base/',
  '@tailwind components',
  '@tailwind utilities',
  'utilities.css',
];

function rank(entry) {
  const s = entry.spec;
  if (s.startsWith('tokens/') || s.includes('/tokens/')) return 0;
  if (s === '@tailwind base') return 1;
  if (s.startsWith('base/') || s.includes('/base/')) return 2;
  if (s === '@tailwind components') return 3;
  if (s === '@tailwind utilities') return 4;
  if (s.endsWith('utilities.css')) return 5;
  return 3.5; // 未知项夹在中间
}

let errors = 0;
let prev = -1;
for (const e of imports) {
  const r = rank(e);
  if (r < prev) {
    console.error(`ERR  index.css:${e.line}  「${e.spec}」位置过晚（应在它前面）`);
    errors++;
  }
  prev = Math.max(prev, r);
}

// 必需的项必须存在
const specs = new Set(imports.map((e) => e.spec));
for (const must of ['@tailwind base', '@tailwind components', '@tailwind utilities']) {
  if (!specs.has(must)) {
    console.error(`ERR  index.css 缺少「${must}」`);
    errors++;
  }
}
if (![...specs].some((s) => s.endsWith('utilities.css'))) {
  console.warn('WARN  index.css 未引入 utilities.css');
}

if (errors > 0) {
  console.error(`\n✗ @import 顺序错误（${errors} 项）`);
  process.exit(1);
}
console.log(`✓ @import 顺序正确（${imports.length} 条）`);

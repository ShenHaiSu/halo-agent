// src/Web.Client/scripts/check-font-stack.mjs
/**
 * 校验字体栈是否覆盖 Win7。
 * ============ 为什么 ============
 *  Win7 没有：Segoe UI Variable / SF Pro / Inter（未装时）/ 任何彩色 emoji 字体
 *  Win7 有：Segoe UI / Microsoft YaHei / 宋体 / Consolas / Courier New
 *  如果字体栈只写 "Inter, system-ui"，Win7 上会落到"宋体"，观感直接崩。
 * ============ ============
 */
import { readFile } from 'node:fs/promises';
import { join } from 'node:path';
import { fileURLToPath } from 'node:url';

const ROOT = fileURLToPath(new URL('../', import.meta.url));
const text = await readFile(join(ROOT, 'src/styles/tokens/typography.css'), 'utf8');

const MUST = [
  { name: 'UI 字体栈含 Microsoft YaHei', re: /--font-sans\s*:[^;]*Microsoft YaHei/i },
  { name: 'UI 字体栈含 Segoe UI', re: /--font-sans\s*:[^;]*Segoe UI/i },
  { name: '等宽字体栈含 Consolas', re: /--font-mono\s*:[^;]*Consolas/i },
  { name: '等宽字体栈含中文回退', re: /--font-mono\s*:[^;]*(Microsoft YaHei|SimSun)/i },
  { name: 'sans 栈有 sans-serif 兜底', re: /--font-sans\s*:[^;]*sans-serif/i },
  { name: 'mono 栈有 monospace 兜底', re: /--font-mono\s*:[^;]*monospace/i },
];

let errors = 0;
for (const m of MUST) {
  if (!m.re.test(text)) {
    console.error(`ERR  ${m.name}`);
    errors++;
  }
}

// 禁用项
const FORBID = [
  { re: /Segoe UI Variable/i, why: 'Win7 没有' },
  { re: /Inter\b(?!.*fallback)/i, why: '未随包分发时 Win7 无此字体（若作为首选且未内嵌，需确认）' },
];
for (const f of FORBID) {
  if (f.re.test(text)) console.warn(`WARN  字体栈出现「${f.re.source}」：${f.why}`);
}

if (errors > 0) {
  console.error(`\n✗ 字体栈不完整（${errors} 项）`);
  process.exit(1);
}
console.log('✓ 字体栈覆盖 Win7');

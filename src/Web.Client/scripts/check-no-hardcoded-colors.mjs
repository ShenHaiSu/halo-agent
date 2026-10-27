// src/Web.Client/scripts/check-no-hardcoded-colors.mjs
/**
 * 检查组件里是否硬编码了颜色（铁律 F-4）。
 *
 * ============ 允许的例外 ============
 *  ① src/styles/tokens/*.css（定义变量的地方）
 *  ② 测试文件
 *  ③ 注释行
 *  ④ 明确标注了 eslint-disable 或 @allow-hardcoded-color 的行
 * ============ ============
 */
import { readdir, readFile } from 'node:fs/promises';
import { join, extname, relative } from 'node:path';
import { fileURLToPath } from 'node:url';

const ROOT = fileURLToPath(new URL('../', import.meta.url));
const SRC = join(ROOT, 'src');

const ALLOW_DIRS = ['styles/tokens', 'styles/themes', '__gallery', 'theme/builtin', 'theme'];
const ALLOW_FILES = ['.test.ts', '.spec.ts'];

/** 匹配 #rgb / #rrggbb / #rrggbbaa / rgb() / rgba() / hsl() / hsla() */
const COLOR_PATTERNS = [
  /#[0-9a-fA-F]{3}\b/g,
  /#[0-9a-fA-F]{6}\b/g,
  /#[0-9a-fA-F]{8}\b/g,
  /\brgba?\s*\(/g,
  /\bhsla?\s*\(/g,
];

const ALLOW_MARK = '@allow-hardcoded-color';

let errors = 0;

async function walk(dir) {
  const out = [];
  for (const e of await readdir(dir, { withFileTypes: true })) {
    const p = join(dir, e.name);
    if (e.isDirectory()) out.push(...(await walk(p)));
    else out.push(p);
  }
  return out;
}

const files = await walk(SRC);
let scanned = 0;

for (const f of files) {
  const rel = relative(SRC, f).replace(/\\/g, '/');
  if (!['.vue', '.ts', '.css', '.scss'].includes(extname(f))) continue;
  if (ALLOW_DIRS.some((d) => rel.startsWith(d))) continue;
  if (ALLOW_FILES.some((s) => rel.includes(s))) continue;

  scanned++;
  const text = await readFile(f, 'utf8');
  const lines = text.split('\n');

  lines.forEach((raw, i) => {
    const line = raw.trim();
    // 跳过注释
    if (line.startsWith('//') || line.startsWith('*') || line.startsWith('/*')) return;
    if (raw.includes(ALLOW_MARK)) return;

    // hsl(var(--x)) / rgb(var(--x)) 是正确 token 用法，放行
    if (raw.includes('hsl(var(--') || raw.includes('hsla(var(--') || raw.includes('hsl( var(--'))
      return;
    if (raw.includes('rgb(var(--') || raw.includes('rgba(var(--')) return;
    for (const pat of COLOR_PATTERNS) {
      pat.lastIndex = 0;
      if (pat.test(raw)) {
        console.error(`ERR  src/${rel}:${i + 1}  硬编码颜色：${line.slice(0, 100)}`);
        console.error(`     → 请改用 token：var(--accent) / bg-accent / text-fg 等`);
        errors++;
      }
    }
  });
}

console.log(`\n扫描了 ${scanned} 个文件`);
if (errors > 0) {
  console.error(`✗ 发现 ${errors} 处硬编码颜色（违反铁律 F-4）`);
  process.exit(1);
}
console.log('✓ 无硬编码颜色');

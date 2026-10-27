// src/Web.Client/scripts/check-token-parity.mjs
/**
 * 检查 tokens/colors.css 与 theme/builtin/light.ts 的数值是否一致。
 *
 * ============ 为什么必须检查 ============
 *  这两份数据是重复的（一份给 CSS 首帧，一份给 JS 切换）。
 *  一旦有人改了其中一份，就会出现：
 *    "首次启动是 A 色，切到深色再切回浅色是 B 色"
 *  这是最难发现、最难解释的 bug。
 * ============ ============
 */
import { readFile } from 'node:fs/promises';
import { join } from 'node:path';
import { fileURLToPath } from 'node:url';

const ROOT = fileURLToPath(new URL('../', import.meta.url));

const CSS_FILE = join(ROOT, 'src/styles/tokens/colors.css');
const TS_FILE = join(ROOT, 'src/theme/builtin/light.ts');

/** 从 CSS 提取 `--name: value;` */
function parseCss(text) {
  const map = {};
  // 排除注释
  const clean = text.replace(/\/\*[\s\S]*?\*\//g, '');
  const re = /--([\w-]+)\s*:\s*([^;]+);/g;
  let m;
  while ((m = re.exec(clean)) !== null) {
    map[m[1]] = m[2].trim();
  }
  return map;
}

/** 从 TS 提取 `'name': 'value',` */
function parseTs(text) {
  const map = {};
  // 只从 tokens: { ... } 里提取
  const start = text.indexOf('tokens:');
  if (start < 0) return map;
  const body = text.slice(start);
  const re = /(?:['"])?([\w-]+)(?:['"])?\s*:\s*['"]([^'"]+)['"]/g;
  let m;
  while ((m = re.exec(body)) !== null) {
    // shadow 值里含引号外的内容（没有），这里简单处理
    map[m[1]] = m[2].trim();
  }
  return map;
}

/**
 * 归一化：把空格压成一个，去掉首尾空格，数值 0.10/0.30 与 0.1/0.3 等价（prettier 会压 CSS 小数）。
 */
function normNum(s) {
  return String(s).replace(/\d+\.\d+/g, (m) => String(parseFloat(m)));
}
function norm(v) {
  return normNum(String(v))
    .replace(/\s+/g, ' ')
    .replace(/\s*\/\s*/g, ' / ')
    .trim();
}

async function main() {
  const cssText = await readFile(CSS_FILE, 'utf8');
  const tsText = await readFile(TS_FILE, 'utf8');

  const css = parseCss(cssText);
  const ts = parseTs(tsText);

  // bg-overlay 同时存在于 CSS 与 light.ts（30 项一致），不删除

  const allKeys = new Set([...Object.keys(css), ...Object.keys(ts)]);
  let errors = 0;

  for (const key of [...allKeys].sort()) {
    const c = css[key];
    const t = ts[key];
    if (c === undefined) {
      console.error(`ERR  ${key}：CSS 里没有，TS 里有「${t}」`);
      errors++;
    } else if (t === undefined) {
      console.error(`ERR  ${key}：TS 里没有，CSS 里有「${c}」`);
      errors++;
    } else if (norm(c) !== norm(t)) {
      console.error(`ERR  ${key}：CSS=「${c}」 TS=「${t}」`);
      errors++;
    }
  }

  if (errors > 0) {
    console.error(`\n✗ token 不一致 ${errors} 处`);
    console.error('请让 src/styles/tokens/colors.css 与 src/theme/builtin/light.ts 保持一致。');
    process.exit(1);
  }
  console.log(`✓ token 一致（${Object.keys(ts).length} 项）`);
}

main().catch((e) => {
  console.error(e);
  process.exit(1);
});

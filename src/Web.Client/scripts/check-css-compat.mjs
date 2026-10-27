// src/Web.Client/scripts/check-css-compat.mjs
/**
 * 扫描前端产物，检出 Chrome 109 不支持的语法。
 *
 * ============ 为什么必须做这一层 ============
 *  esbuild/Vite 不会降级 CSS 颜色函数——`oklch()` 是合法 CSS，
 *  工具认为"这就是它该有的样子"，原样输出。
 *  但 Chrome 109 不认识 `oklch()`，遇到时**整条声明失效**（不会整表崩，
 *  但如果出现在关键变量定义里，主题会整体错乱）。
 *  `color-mix()`、`@container style()`、原生的 `&` 嵌套同理。
 * ============ ============
 */
import { readdir, readFile } from 'node:fs/promises';
import { join, extname, relative } from 'node:path';
import { fileURLToPath } from 'node:url';

const ROOT = fileURLToPath(new URL('../', import.meta.url));
const TARGET_DIR = join(ROOT, '../Host.Desktop/wwwroot');

/** @type {{pattern: RegExp, name: string, why: string, severity: 'error'|'warn'}[]} */
const RULES = [
  {
    pattern: /\boklch\s*\(/gi,
    name: 'oklch()',
    why: 'Chrome 111+ 才支持；109 会丢弃整条声明',
    severity: 'error',
  },
  {
    pattern: /\blch\s*\(/gi,
    name: 'lch()',
    why: 'Chrome 111+',
    severity: 'error',
  },
  {
    pattern: /\blab\s*\(/gi,
    name: 'lab()',
    why: 'Chrome 111+',
    severity: 'error',
  },
  {
    pattern: /\bcolor-mix\s*\(/gi,
    name: 'color-mix()',
    why: 'Chrome 111+',
    severity: 'error',
  },
  {
    pattern: /\bcolor\s*\(\s*display-p3/gi,
    name: 'color(display-p3 ...)',
    why: 'Chrome 111+',
    severity: 'error',
  },
  {
    pattern: /@container\s+\w[\w-]*\s+style\s*\(/gi,
    name: '@container style()',
    why: 'Chrome 110+；109 只支持尺寸容器查询',
    severity: 'error',
  },
  {
    pattern: /@starting-style/gi,
    name: '@starting-style',
    why: 'Chrome 117+',
    severity: 'error',
  },
  {
    pattern: /text-wrap\s*:\s*(balance|pretty)/gi,
    name: 'text-wrap: balance/pretty',
    why: 'Chrome 114+',
    severity: 'warn',
  },
  {
    pattern: /scrollbar-width\s*:|scrollbar-color\s*:/gi,
    name: 'scrollbar-width / scrollbar-color',
    why: 'Chrome 121+；请用 ::-webkit-scrollbar',
    severity: 'error',
  },
  {
    pattern: /:nth-child\s*\([^)]*\bof\b/gi,
    name: ':nth-child(... of S)',
    why: 'Chrome 111+',
    severity: 'error',
  },
  {
    pattern: /view-transition-name\s*:/gi,
    name: 'view-transition-name',
    why: 'Chrome 111+',
    severity: 'error',
  },
  {
    pattern: /@property\s+--/gi,
    name: '@property',
    why: '109 对 color 类型支持不完整；本项目约定不使用',
    severity: 'warn',
  },
  {
    pattern: /\banchor-name\s*:/gi,
    name: 'anchor-name / CSS Anchor Positioning',
    why: 'Chrome 125+',
    severity: 'error',
  },
  {
    pattern: /@scope\s*\(/gi,
    name: '@scope',
    why: 'Chrome 118+',
    severity: 'error',
  },
  {
    pattern: /\bfield-sizing\s*:/gi,
    name: 'field-sizing',
    why: 'Chrome 123+',
    severity: 'warn',
  },
  {
    pattern: /interpolate-size\s*:/gi,
    name: 'interpolate-size',
    why: 'Chrome 129+',
    severity: 'error',
  },
];

/** JS 侧禁用 API（产物里出现即说明用了） */
const JS_RULES = [
  {
    pattern: /\bObject\.groupBy\b/g,
    name: 'Object.groupBy',
    why: 'Chrome 117+',
    severity: 'error',
  },
  { pattern: /\bMap\.groupBy\b/g, name: 'Map.groupBy', why: 'Chrome 117+', severity: 'error' },
  {
    pattern: /\bPromise\.withResolvers\b/g,
    name: 'Promise.withResolvers',
    why: 'Chrome 119+',
    severity: 'error',
  },
  {
    pattern: /\bArray\.fromAsync\b/g,
    name: 'Array.fromAsync',
    why: 'Chrome 121+',
    severity: 'error',
  },
  {
    pattern: /\.toSorted\s*\(/g,
    name: 'Array.prototype.toSorted',
    why: 'Chrome 110+',
    severity: 'error',
    // Vue 3.5 运行时的响应式数组转发（仅业务主动调用才执行；lint+TS lib 已禁）。vue vendor 块跳过，见 scan。
    vendorForwardAllow: true,
  },
  {
    pattern: /\.toReversed\s*\(/g,
    name: 'Array.prototype.toReversed',
    why: 'Chrome 110+',
    severity: 'error',
    vendorForwardAllow: true,
  },
  {
    pattern: /\.toSpliced\s*\(/g,
    name: 'Array.prototype.toSpliced',
    why: 'Chrome 110+',
    severity: 'error',
    vendorForwardAllow: true,
  },
  {
    pattern: /navigator\.gpu\b/g,
    name: 'WebGPU',
    why: 'Chrome 113+ 且 Win7 无支持',
    severity: 'error',
  },
  { pattern: /showPicke?r?\s*\(/g, name: 'Popover API', why: 'Chrome 114+', severity: 'error' },
];

let errorCount = 0;
let warnCount = 0;

/**
 * @param {string} file
 * @param {string} text
 * @param {{pattern: RegExp, name: string, why: string, severity: 'error'|'warn'}[]} rules
 */
function scan(file, text, rules) {
  const rel = relative(ROOT, file);
  for (const rule of rules) {
    if (rule.vendorForwardAllow && /vue-[\w.-]*\.js$/.test(file)) continue;
    rule.pattern.lastIndex = 0;
    let m;
    while ((m = rule.pattern.exec(text)) !== null) {
      // 计算行号
      const before = text.slice(0, m.index);
      const line = before.split('\n').length;
      const col = m.index - before.lastIndexOf('\n');
      const msg = `${rule.severity === 'error' ? 'ERR ' : 'WARN'} ${rel}:${line}:${col}  ${rule.name}  — ${rule.why}`;
      if (rule.severity === 'error') {
        console.error(msg);
        errorCount++;
      } else {
        console.warn(msg);
        warnCount++;
      }
      // 避免同一模式无限循环
      if (m.index === rule.pattern.lastIndex) rule.pattern.lastIndex++;
    }
  }
}

async function walk(dir) {
  /** @type {string[]} */
  const out = [];
  for (const entry of await readdir(dir, { withFileTypes: true })) {
    const p = join(dir, entry.name);
    if (entry.isDirectory()) out.push(...(await walk(p)));
    else out.push(p);
  }
  return out;
}

async function main() {
  let files;
  try {
    files = await walk(TARGET_DIR);
  } catch {
    console.error(`找不到产物目录：${TARGET_DIR}`);
    console.error('请先运行 pnpm build');
    process.exit(1);
  }

  const css = files.filter((f) => extname(f) === '.css');
  const js = files.filter((f) => extname(f) === '.js' || extname(f) === '.mjs');
  const html = files.filter((f) => extname(f) === '.html');

  console.log(`扫描 ${css.length} 个 CSS、${js.length} 个 JS、${html.length} 个 HTML…`);

  for (const f of css) scan(f, await readFile(f, 'utf8'), RULES);
  for (const f of js) scan(f, await readFile(f, 'utf8'), JS_RULES);
  for (const f of html) {
    const t = await readFile(f, 'utf8');
    scan(f, t, RULES);
    scan(f, t, JS_RULES);
    // 额外：HTML 里不能有 file:// 绝对引用
    if (/file:\/\//.test(t)) {
      console.error(`ERR  ${relative(ROOT, f)}  含 file:// 引用`);
      errorCount++;
    }
  }

  console.log('');
  if (errorCount > 0) {
    console.error(`✗ 发现 ${errorCount} 个错误、${warnCount} 个警告`);
    console.error(
      '这些语法在 Chrome 109（Win7 的 CefSharp 109.1.110）上会导致样式失效或脚本报错。'
    );
    process.exit(1);
  }
  console.log(`✓ 兼容性检查通过（${warnCount} 个警告）`);
}

main().catch((e) => {
  console.error(e);
  process.exit(1);
});

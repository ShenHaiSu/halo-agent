// 源码级断言 —— 语义色透明度修饰（如 border-subtle/50）必须能在 Tailwind 里真正生成规则。
//
// 背景：tailwind.config.js 里自定义色若写成纯 `var(--hex)` 字符串，Tailwind 走
//       withAlphaValue() → parseColor() 时解析失败，返回 undefined，声明被丢弃、规则为空、
//       最终被裁剪：`<utility>-<自定义色>/<数值>` 一个 CSS 规则都不生成，且无任何报错。
//       浏览器于是回落到 preflight 的 border-color:#e5e7eb → 暗色下出现亮灰白边框。
//       该类失效只在「看编译产物」或「肉眼比对主题」时才会暴露，因此用静态守卫兜住。
//
// 做法：
//   1. 读 tailwind.config.js，拿到自定义色表（theme.extend.colors）；
//   2. 扫 src 下所有 .vue / .ts 源码，找出所有「工具类-色板/透明度」写法；
//   3. 逐个判定：
//      · 自定义色是纯 var(--hex) 字符串 → 无法参与透明度修饰，判失败；
//      · 自定义色是 hex / rgb() / hsl() 字面量或函数形式 → Tailwind 原生可解析，放行；
//      · 自定义色是 rgb(var(--x-rgb) / <alpha-value>) → 额外校验 main.css 在暗 / 亮两套主题里
//        都定义了这个 --x-rgb 三元组（缺一套就会出现「某主题下整块色失效」）；
//      · 色板是 Tailwind 内置调色板（amber-500 之类）→ 天然可解析，放行；
//      · 色板既不在自定义色表也不在内置调色板 → 该 token 根本不存在，该类永远不会生成，判失败。
//
// 豁免：命中行含注释 `tailwind-alpha-allow` 时跳过（当前无需使用，留给确有必要的一次性场景）。
//
// 用法：node scripts/assert-tailwind-color-alpha.mjs
// 退出码：0 = 通过；1 = 存在会静默失效的透明度修饰。
import { readFileSync, readdirSync, statSync, existsSync } from 'node:fs';
import { dirname, join, relative, resolve } from 'node:path';
import { fileURLToPath, pathToFileURL } from 'node:url';

const here = dirname(fileURLToPath(import.meta.url));
const clientRoot = resolve(here, '..');
const srcRoot = resolve(clientRoot, 'src');
const configPath = resolve(clientRoot, 'tailwind.config.js');
const cssPath = resolve(srcRoot, 'shared/ui/main.css');

const ALLOW_MARKER = 'tailwind-alpha-allow';

/** 能带 /透明度 修饰的颜色工具前缀（含 ring-offset 这种复合前缀） */
const UTILITY = '(?:bg|text|border|ring-offset|ring|divide|outline|fill|stroke|from|via|to|placeholder|decoration|accent|caret|shadow)';

/** 形如 `hover:bg-elevated/60`：前面允许任意变体前缀，值允许 60 / [0.6] 两种写法 */
const ALPHA_CLASS_RE = new RegExp(
  `(?:^|[\\s"'\`=:[({])((?:(?:[a-zA-Z][\\w-]*):)*)${UTILITY}-([a-z][a-z0-9]*(?:-[a-z0-9]+)*)\\/(-?[\\d.]+|\\[[^\\]]+\\])`,
  'g'
);

/** text-xs/6 这类「字号 + 行高」修饰不是颜色，必须排除，否则误报 */
const NON_COLOR_TOKENS = new Set([
  'xs',
  'sm',
  'base',
  'lg',
  'xl',
  '2xl',
  '3xl',
  '4xl',
  '5xl',
  '6xl',
  '7xl',
  '8xl',
  '9xl',
  'transparent',
  'current',
  'currentcolor',
  'inherit',
  'initial',
  'none',
  'auto',
]);

/** Tailwind v3 内置调色板色名（无后缀即为 500 档）；这些色值本身就是 hex，可正常参与透明度修饰 */
const PALETTE_NAMES = [
  'slate',
  'gray',
  'zinc',
  'neutral',
  'stone',
  'red',
  'orange',
  'amber',
  'yellow',
  'lime',
  'green',
  'emerald',
  'teal',
  'cyan',
  'sky',
  'blue',
  'indigo',
  'violet',
  'purple',
  'fuchsia',
  'pink',
  'rose',
  'black',
  'white',
];
const PALETTE_SHADES = ['50', '100', '200', '300', '400', '500', '600', '700', '800', '900', '950'];

const STOCK_COLORS = new Set(PALETTE_NAMES);
for (const name of PALETTE_NAMES) {
  for (const shade of PALETTE_SHADES) STOCK_COLORS.add(`${name}-${shade}`);
}

/** 可被 Tailwind parseColor 直接解析的色值字面量（透明度修饰天然可用） */
const PARSEABLE_COLOR_RE = /^(?:#(?:[0-9a-f]{3,8})|rgba?\(|hsla?\(|transparent$|currentColor$)/i;

function walk(dir, out) {
  for (const name of readdirSync(dir)) {
    const p = join(dir, name);
    const st = statSync(p);
    if (st.isDirectory()) walk(p, out);
    else if (name.endsWith('.vue') || name.endsWith('.ts')) out.push(p);
  }
  return out;
}

const customColors = await (async () => {
  const mod = await import(pathToFileURL(configPath).href);
  return mod?.default?.theme?.extend?.colors ?? {};
})();

const cssSource = existsSync(cssPath) ? readFileSync(cssPath, 'utf8') : null;

const problems = [];

function pushProblem(file, line, subject, reason) {
  problems.push({ file: relative(clientRoot, file), line, subject, reason });
}

/** 统计某个 CSS 变量在 main.css 里的定义次数：暗色 :root + html.light 各需一次 */
function countVarDefinitions(varName) {
  if (!cssSource) return null;
  return cssSource.split(`${varName}:`).length - 1;
}

// ① 先体检配置层的自定义色
for (const [token, value] of Object.entries(customColors)) {
  if (typeof value === 'function') continue; // 函数式色值自带 opacityValue 通道，无法也不需静态判定
  if (typeof value !== 'string') continue;

  const usesAlphaPlaceholder = value.includes('<alpha-value>');
  const hasVar = /\bvar\(/.test(value);

  if (hasVar && !usesAlphaPlaceholder) {
    pushProblem(
      configPath,
      0,
      `colors.${token}`,
      `值为 "${value}" 是纯 var() 字符串，不含 <alpha-value>，任何 ${token}/xx 透明度修饰都会被静默丢弃；` +
        '应改为 rgb(var(--xxx-rgb) / <alpha-value>)'
    );
    continue;
  }

  if (!hasVar && !PARSEABLE_COLOR_RE.test(value.trim())) {
    pushProblem(
      configPath,
      0,
      `colors.${token}`,
      `值 "${value}" 既不是可解析色值（hex/rgb()/hsl()）也不是 rgb(var(--x-rgb) / <alpha-value>) 形态，` + '该色的透明度修饰无法生成'
    );
    continue;
  }

  if (usesAlphaPlaceholder) {
    const varMatch = value.match(/var\((--[a-zA-Z0-9-]+)\)/);
    if (!varMatch) continue;
    const varName = varMatch[1];
    const hits = countVarDefinitions(varName);
    if (hits !== null && hits < 2) {
      pushProblem(
        cssPath,
        0,
        varName,
        `colors.${token} 依赖 ${varName}，但 main.css 只找到 ${hits} 处定义（暗色 :root 与 html.light 各需 1 处），缺失主题下该色会整块失效`
      );
    }
  }
}

// ② 逐文件扫描源码里的透明度修饰写法
for (const file of walk(srcRoot, [])) {
  const lines = readFileSync(file, 'utf8').split(/\r?\n/);
  for (let i = 0; i < lines.length; i += 1) {
    const line = lines[i];
    if (line.includes(ALLOW_MARKER)) continue;
    for (const m of line.matchAll(ALPHA_CLASS_RE)) {
      const token = m[2];
      if (NON_COLOR_TOKENS.has(token)) continue;
      if (Object.prototype.hasOwnProperty.call(customColors, token)) continue; // ①已单独体检
      if (STOCK_COLORS.has(token)) continue; // Tailwind 内置调色板，可解析
      pushProblem(
        file,
        i + 1,
        m[0].trim(),
        `token "${token}" 既不在 tailwind.config.js 的 theme.extend.colors，也不是内置调色板 → 该类不会生成任何规则`
      );
    }
  }
}

if (problems.length > 0) {
  console.error(`[assert-tailwind-color-alpha] FAIL: ${problems.length} 处语义色透明度修饰无法生成规则：`);
  for (const p of problems) {
    const where = p.line > 0 ? `${p.file}:${p.line}` : p.file;
    console.error(`  - ${where}: ${p.subject} -> ${p.reason}`);
  }
  console.error(
    '  修法：自定义色统一写成 rgb(var(--xxx-rgb) / <alpha-value>)，并在 main.css 的 :root / html.light 定义 --xxx-rgb 三元组。'
  );
  console.error('  背景：docs/pitfalls/20261115-tailwind自定义色透明度修饰静默失效.md');
  process.exit(1);
}

const tokenCount = Object.keys(customColors).length;
console.log(`[assert-tailwind-color-alpha] OK: ${tokenCount} 个自定义色均支持透明度修饰，源码中未发现会静默失效的写法`);

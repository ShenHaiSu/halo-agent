// src/Web.Client/scripts/check-pins.mjs
/**
 * 关键依赖的版本锁定检查。
 *
 * ============ 为什么必须锁 ============
 *  ① tailwindcss：v4 产出 oklch()/color-mix() → Chrome 109 白屏（R-3）
 *  ② vue / vite 等：大版本跳跃会改编译产物（如 vue 3.6 的 vapor 模式）
 *  ③ .NET 侧的 CefSharp 109.1.110 已在 P0/P3 锁定，这里只管前端
 * ============ 规则 ============
 *  EXACT：值必须完全等于指定版本（不许 ^ ~ >=）
 *  TILDE：值必须是 ~x.y.z（锁 minor）
 * ============ ============
 */
import { readFile } from 'node:fs/promises';
import { join } from 'node:path';
import { fileURLToPath } from 'node:url';

const ROOT = fileURLToPath(new URL('../', import.meta.url));

const EXACT = {
  tailwindcss: '3.4.16',
};

const TILDE = {
  vue: '3.5.',
  'vue-router': '4.4.',
  pinia: '2.2.',
  '@vueuse/core': '11.3.',
  vite: '5.4.',
  typescript: '5.6.',
  'vue-tsc': '2.1.',
  vitest: '2.1.',
  'eslint-plugin-vue': '9.31.',
  'typescript-eslint': '8.16.',
  prettier: '3.4.',
  jsdom: '25.',
  '@vitejs/plugin-vue': '5.2.',
  '@vue/test-utils': '2.4.',
};

const FORBIDDEN_DEPS = [
  // 明确禁用的依赖（用了会在 Chrome 109 上出问题）
  {
    name: 'tailwindcss',
    versionPrefix: '4.',
    why: 'v4 基线 Chrome 111+，产出 oklch()/color-mix()',
  },
  { name: 'core-js', versionPrefix: '', why: '不需要（三个缺失 API 手写 3 行即可）' },
];

const pkg = JSON.parse(await readFile(join(ROOT, 'package.json'), 'utf8'));
const all = { ...pkg.dependencies, ...pkg.devDependencies, ...pkg.peerDependencies };

let errors = 0;

for (const [name, want] of Object.entries(EXACT)) {
  const got = all[name];
  if (got === undefined) {
    console.error(`ERR  ${name} 缺失（应为精确版本 "${want}"）`);
    errors++;
    continue;
  }
  if (got !== want) {
    console.error(
      `ERR  ${name} 必须是精确版本 "${want}"，实际 "${got}"。` +
        `去掉 ${got.replace(/[\d.]+$/, '') || ''} 前缀。`
    );
    errors++;
  }
}

for (const [name, prefix] of Object.entries(TILDE)) {
  const got = all[name];
  if (got === undefined) continue; // 可选依赖不报错
  const bare = typeof got === 'string' ? got.replace(/^[~^>=<\s]+/, '') : '';
  const ok =
    typeof got === 'string' &&
    ((got.startsWith('~') && got.slice(1).startsWith(prefix)) ||
      (bare.startsWith(prefix) && !/^[~^>=]/.test(got)));
  if (!ok) {
    console.error(`ERR  ${name} 应为 "~${prefix}x"，实际 "${got}"`);
    errors++;
  }
}

for (const bad of FORBIDDEN_DEPS) {
  // 注：与原文的 includes() 不同，这里剥离范围前缀后用 startsWith 比较。
  // 原文写法会对规范值误报："3.4.16".includes("4.") === true（"3.[4.]16"）。
  const raw = all[bad.name];
  const core = raw === undefined ? '' : String(raw).replace(/^[~^>=<\s]*/, '');
  if (raw !== undefined && (!bad.versionPrefix || core.startsWith(bad.versionPrefix))) {
    console.error(`ERR  禁止依赖 ${bad.name}（${bad.why}）`);
    errors++;
  }
}

// 传递依赖里出现 tailwindcss v4 也要拦
const lockPath = join(ROOT, 'pnpm-lock.yaml');
try {
  const lock = await readFile(lockPath, 'utf8');
  if (/^\s{2}tailwindcss@4\./m.test(lock)) {
    console.error('ERR  lockfile 中解析到了 tailwindcss v4（某个包把它当依赖拉进来了）');
    errors++;
  }
} catch {
  console.warn('WARN  未找到 pnpm-lock.yaml，跳过传递依赖检查');
}

if (errors > 0) {
  console.error(`\n✗ 版本锁定检查失败（${errors} 项）`);
  process.exit(1);
}
console.log('✓ 版本锁定正确');

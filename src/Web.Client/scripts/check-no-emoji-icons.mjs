// src/Web.Client/scripts/check-no-emoji-icons.mjs
/**
 * 检查是否用 emoji 当图标（铁律 F-6）。
 *
 * ============ 为什么 Win7 上 emoji 是灾难 ============
 *  Win7 没有彩色 emoji 字体（Segoe UI Emoji 从 Win8 才有）。
 *  在 Win7 上，emoji 会被渲染成黑白方块、豆腐块或直接被吞掉。
 *  所以图标必须是 SVG。
 * ============ 允许的例外 ============
 *  ① 用户消息内容（可能是用户自己发的 emoji）
 *  ② 模型回复内容（同上）
 *  ③ 测试数据
 * ============ ============
 *
 * 注：原文的 shebang 行已省略（shebang 必须在文件第 1 行才有效，
 * 本文件以路径注释开头，故直接用 `node scripts/*.mjs` 运行）。
 */
import { readdir, readFile } from 'node:fs/promises';
import { join, extname, relative } from 'node:path';
import { fileURLToPath } from 'node:url';

const ROOT = fileURLToPath(new URL('../', import.meta.url));
const SRC = join(ROOT, 'src');

/**
 * 覆盖常见 emoji 区段。
 * 用码点构造字符类，避免源文件直接含非 BMP 字符：
 * U+1F300–U+1F9FF（杂项符号/表情）、U+1F000–U+1F2FF（麻将/扑克等）、
 * U+2600–U+27BF（杂项符号/装饰）、U+FE0F（变体选择符）、U+2B00–U+2BFF（箭头补充）。
 */
const cp = String.fromCodePoint;
const EMOJI_RE = new RegExp(
  `[${cp(0x1f300)}-${cp(0x1f9ff)}${cp(0x1f000)}-${cp(0x1f2ff)}${cp(0x2600)}-${cp(0x27bf)}${cp(0xfe0f)}${cp(0x2b00)}-${cp(0x2bff)}]`,
  'gu'
);

/** 允许出现 emoji 的文件/目录（用户内容渲染处） */
const ALLOW = ['components/chat/MessageContent.vue', '__gallery', '.test.ts', 'mock/'];

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

for (const f of await walk(SRC)) {
  const rel = relative(SRC, f).replace(/\\/g, '/');
  if (!['.vue', '.ts'].includes(extname(f))) continue;
  if (ALLOW.some((a) => rel.includes(a))) continue;

  const lines = (await readFile(f, 'utf8')).split('\n');
  lines.forEach((line, i) => {
    EMOJI_RE.lastIndex = 0;
    const m = EMOJI_RE.exec(line);
    if (m) {
      console.error(`ERR  src/${rel}:${i + 1}  发现 emoji "${m[0]}"`);
      console.error(`     → 请改用 src/components/icons/ 下的 SVG 组件`);
      errors++;
    }
  });
}

if (errors > 0) {
  console.error(`\n✗ 发现 ${errors} 处 emoji（违反铁律 F-6，Win7 上会显示为豆腐块）`);
  process.exit(1);
}
console.log('✓ 无 emoji 图标');

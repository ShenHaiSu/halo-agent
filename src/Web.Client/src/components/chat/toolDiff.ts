// src/Web.Client/src/components/chat/toolDiff.ts
// P11-F diff 块提取（ToolDiff.vue 的纯函数部分，可单测）。
import { diffLines, type DiffLine } from '@/utils/diff';

/** P11-F 上限：合并行数超 400 行截断（对齐 ToolOutput 4000字 / CodeBlock 30行 的折叠语言）。 */
export const DIFF_MAX_LINES = 400;

/**
 * 从 unified diff 文本还原 before/after，再走 diffLines 行级渲染。
 * 跳过 diff/index/---/+++/@@ 头行与 "\ No newline" 标记。
 */
export function blockToLines(block: string): DiffLine[] {
  const before: string[] = [];
  const after: string[] = [];
  for (const raw of block.split('\n')) {
    if (
      raw.startsWith('diff ') ||
      raw.startsWith('index ') ||
      raw.startsWith('+++ ') ||
      raw.startsWith('--- ') ||
      raw.startsWith('@@ ') ||
      raw.startsWith('\\ No newline')
    ) {
      continue;
    }
    if (raw.startsWith('+') && !raw.startsWith('+++')) {
      after.push(raw.slice(1));
    } else if (raw.startsWith('-') && !raw.startsWith('---')) {
      before.push(raw.slice(1));
    } else if (raw.startsWith(' ')) {
      before.push(raw.slice(1));
      after.push(raw.slice(1));
    } else if (raw === '') {
      continue;
    } else {
      before.push(raw);
      after.push(raw);
    }
  }
  if (before.length === 0 && after.length === 0) return [];
  return diffLines(before.join('\n'), after.join('\n'));
}

/** 围栏 ```diff 块（现状优先级最高）。 */
export function extractFencedBlocks(text: string): string[] {
  const out: string[] = [];
  const re = /```diff\r?\n([\s\S]*?)```/g;
  let m: RegExpExecArray | null;
  while ((m = re.exec(text)) !== null) {
    if (m[1] && m[1].trim()) out.push(m[1]);
  }
  return out;
}

const BARE_START_RE = /^(diff --git |@@ |--- )/;
const BARE_CONT_RE =
  /^(diff --git |index |--- |\+\+\+ |@@ |[+-] |[+-][^ +-]|\\ No newline)/;

/**
 * P11-F 裸 unified diff 块扫描（无围栏时）。
 * 起始：`diff --git ` / `@@ ` / `--- `；续行：diff 头行与 +/- 内容行；
 * 空行视为块内上下文保留（尾部空行裁掉）。成块条件：含至少一个 @@ 或
 * (--- + +++) 或 diff --git，且至少一行 +/- 改动行。
 */
export function extractBareBlocks(text: string): string[] {
  const lines = text.split('\n');
  const blocks: string[] = [];
  let cur: string[] = [];
  const flush = (): void => {
    while (cur.length > 0 && cur[cur.length - 1]!.trim() === '') cur.pop();
    if (cur.length === 0) return;
    const joined = cur.join('\n');
    const hasHeader =
      /^diff --git /m.test(joined) ||
      /^@@ /m.test(joined) ||
      (/^--- /m.test(joined) && /^\+\+\+ /m.test(joined));
    const plusMinus = cur.some((l) => l.startsWith('+') || l.startsWith('-'));
    if (hasHeader && plusMinus) blocks.push(joined);
    cur = [];
  };
  for (const line of lines) {
    if (cur.length === 0) {
      if (BARE_START_RE.test(line)) cur.push(line);
      continue;
    }
    if (BARE_CONT_RE.test(line) || line.trim() === '') cur.push(line);
    else flush();
  }
  flush();
  return blocks;
}

/** 提取放宽（按优先级）：① ```diff 围栏 ② 裸 diff 块；都没有 → 空数组（不渲染）。 */
export function extractDiffBlocks(text: string): string[] {
  const fenced = extractFencedBlocks(text);
  if (fenced.length > 0) return fenced;
  return extractBareBlocks(text);
}

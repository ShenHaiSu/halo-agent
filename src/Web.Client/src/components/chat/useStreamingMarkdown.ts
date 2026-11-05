// src/Web.Client/src/components/chat/useStreamingMarkdown.ts
// 统一稳态渲染。唯一真相为全量 parseMarkdown；流式只做节流渐进 + 块级 holdback。
// 移除旧增量分支（流稳不一致根因）。
import { computed, ref, watch } from 'vue';
import {
  parseMarkdown,
  type MdNode,
  __internal,
} from '@/components/chat/MarkdownRenderer';

export function useStreamingMarkdown(getText: () => string, throttleMs = 200) {
  const nodes = ref<MdNode[]>([]);
  const pendingText = ref('');
  let timer: ReturnType<typeof setTimeout> | null = null;

  function recompute(text: string): void {
    const lastNl = text.lastIndexOf('\n');
    const head = lastNl >= 0 ? text.slice(0, lastNl + 1) : '';
    const cut = head ? holdbackCut(head) : 0;
    const safeHead = head.slice(0, cut);
    nodes.value = safeHead ? parseMarkdown(safeHead, { lenient: true }) : [];
    pendingText.value = text.slice(cut);
  }

  function schedule(text: string): void {
    // EventBus 已按 80ms 合并，前端同帧直接重算（单次全量<=16ms，见性能预算）；
    // 节流由上游保证，此处保持同步以满足流稳一致。
    void timer; void throttleMs;
    recompute(text);
  }

  watch(getText, (t) => schedule(t), { immediate: true });

  return {
    nodes: computed(() => nodes.value),
    pendingText,
    /** 流结束/稳态：全量纠正并替换 */
    finalize() {
      if (timer) {
        clearTimeout(timer);
        timer = null;
      }
      const text = getText();
      nodes.value = text ? parseMarkdown(text) : [];
      pendingText.value = '';
    },
    reset() {
      if (timer) {
        clearTimeout(timer);
        timer = null;
      }
      nodes.value = [];
      pendingText.value = '';
    },
  };
}

/**
 * 提交边界：末尾未完块暂不提交，返回可提交的字符数。
 */
export function holdbackCut(head: string): number {
  const lines = head.split('\n');
  const end = lines[lines.length - 1] === '' ? lines.length - 1 : lines.length;
  let openIdx = -1;
  let openCh = '';
  let openLen = 0;
  for (let i = 0; i < end; i++) {
    const fence = /^ {0,3}(`{3,}|~{3})/.exec(lines[i]!);
    if (!fence) continue;
    const ch = fence[1]![0]!;
    const len = fence[1]!.length;
    if (openIdx >= 0 && ch === openCh && len >= openLen) {
      openIdx = -1;
    } else if (openIdx < 0) {
      openIdx = i;
      openCh = ch;
      openLen = len;
    }
  }
  if (openIdx >= 0) return offsetOfLine(lines, openIdx);
  let i = end - 1;
  while (i >= 0 && isContinuableLine(lines[i]!)) i--;
  const chunkStart = i + 1;
  if (chunkStart >= end) return head.length;
  const first = lines[chunkStart]!;
  if (!__internal.isBlockStart(first) && !/^\s*\|/.test(first)) return head.length;
  if (/^ {0,3}#{1,6}\s/.test(first)) return head.length;
  if (/^ {0,3}(?:(?:\*\s*){3,}|(?:-\s*){3,}|(?:_\s*){3,})$/.test(first)) return head.length;
  if (end - chunkStart > 40) return head.length;
  return offsetOfLine(lines, chunkStart);
}

function isContinuableLine(line: string): boolean {
  if (line.trim() === '') return false;
  if (/^ {0,3}(`{3,}|~{3})/.test(line)) return false;
  if (/^ {0,3}#{1,6}\s/.test(line)) return false;
  if (/^ {0,3}(?:(?:\*\s*){3,}|(?:-\s*){3,}|(?:_\s*){3,})$/.test(line)) return false;
  if (__internal.isListOrQuoteStart(line)) return true;
  if (/^( +|\t)/.test(line)) return true;
  if (/^\s*\|/.test(line)) return true;
  return false;
}

function offsetOfLine(lines: string[], idx: number): number {
  let off = 0;
  for (let i = 0; i < idx; i++) off += lines[i]!.length + 1;
  return off;
}

// src/Web.Client/src/components/chat/useStreamingMarkdown.ts
// P5 D5-3 流式增量 Markdown 渲染（见 01 §5.3）。
//
// 为什么不能每次重新解析：一次回答 8000 字分成 800 个 delta，
// 每次重解析全文 O(n²)，Win7 低端 CPU 直接卡死。
// 正确做法：只解析"新增的完整行"，把不完整的最后一行当纯文本。
//
// 关键不变式：
//   committed：已解析完的完整行（不含最后的半行）
//   pendingText：最后的半行（不解析，原样当 text 节点）
//   新文本必须以旧文本为前缀，否则（用户编辑/重试）全量重解析。
//
// P11-G1：块起始行（fence/quote/list，复用 isBlockStart）到达前不提交上一段——
//   把"提交边界"从"最后一个 \n"推迟到"块完整"，避免增量孤立解析 + mergeAdjacent
//   只合 paragraph 导致的列表/引用跨块闪错。finalize 仍全量纠正。
// P11-G2：增量路径显式传 { lenient: true }（未闭合围栏回退为段落，避免半个 fence
//   闪出"生成中"代码框）；finalize 与非流式保持 lenient: false。

import { ref, watch } from 'vue';
import {
  parseMarkdown,
  type MdNode,
  __internal,
} from '@/components/chat/MarkdownRenderer';

export function useStreamingMarkdown(getText: () => string) {
  const committed = ref<MdNode[]>([]);
  const pendingText = ref('');
  let lastSource = '';
  let lastCommittedLen = 0;

  function recompute(text: string, forceFull = false): void {
    // 不是追加（前缀不符）→ 全量重解析
    if (forceFull || !text.startsWith(lastSource)) {
      const lastNl = text.lastIndexOf('\n');
      const head = lastNl >= 0 ? text.slice(0, lastNl + 1) : '';
      const cut = head ? holdbackCut(head) : head.length;
      const safeHead = head.slice(0, cut);
      // P11-G2：增量路径宽容模式
      committed.value = safeHead ? compact(parseMarkdown(safeHead, { lenient: true })) : [];
      pendingText.value = text.slice(cut);
      lastCommittedLen = cut;
      lastSource = text;
      return;
    }

     // 追加：提交边界按全文 head 计算（跨 delta 的列表/引用块才能整体 holdback），
     // 单调不回退（文本只追加，cutFull >= lastCommittedLen 恒成立）。
     const lastNl = text.lastIndexOf('\n');
     if (lastNl < lastCommittedLen) {
       // 还没有新的完整行，只更新 pending
       pendingText.value = text.slice(lastCommittedLen);
       lastSource = text;
       return;
     }

     const fullHead = text.slice(0, lastNl + 1);
     const cutFull = holdbackCut(fullHead);
     const cut = Math.max(cutFull, lastCommittedLen);
     const safeNew = text.slice(lastCommittedLen, cut);
     // P11-G2：增量路径宽容模式（未闭合围栏回退为段落）
     const deltaNodes = safeNew ? parseMarkdown(safeNew, { lenient: true }) : [];

     committed.value = mergeAdjacent(committed.value.concat(deltaNodes));

     pendingText.value = text.slice(cut);
     lastCommittedLen = cut;
     lastSource = text;
   }

   watch(getText, (t) => recompute(t), { immediate: true });

  return {
    committed,
    pendingText,
    /** 流结束后调用：做一次完整解析并替换 */
    finalize() {
      const text = getText();
      committed.value = parseMarkdown(text);
      pendingText.value = '';
      lastCommittedLen = text.length;
      lastSource = text;
    },
    /** 用户编辑/重试时调用 */
    reset() {
      committed.value = [];
      pendingText.value = '';
      lastCommittedLen = 0;
      lastSource = '';
     },
   };
 }

 /**
  * P11-G1 提交边界：末尾未完块暂不提交，返回可提交的字符数。
  * - 未闭合 fence：从其起始行起 holdback（fence 可含空行，优先配对判定）；
  * - 否则从末尾向前回溯“可延续行”（列表项 / 引用 / 缩进续行 / 表格行），
  *   遇到空行 / fence 行 / 标题 / 分隔线 / 普通段落行即停；
  * - 回溯块首行为 quote/list/fence/table 起始则整块 holdback，等空行或块结束；
  *   首行为普通段落/标题/分隔线 → 整 head 可提交；
  * - holdback 超过 40 行则放行（流式进度优先，finalize 纠正）。
  */
 export function holdbackCut(head: string): number {
   const lines = head.split('\n');
   // head 以 \n 结尾时末元素为 ''，不参与
   const end = lines[lines.length - 1] === '' ? lines.length - 1 : lines.length;
   // ① fence 配对：最后一个未闭合的 fence 起始行
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
   // ② 从末尾向前回溯可延续行，定位末尾未完块的起始
   let i = end - 1;
   while (i >= 0 && isContinuableLine(lines[i]!)) i--;
   const chunkStart = i + 1;
   if (chunkStart >= end) return head.length;
   const first = lines[chunkStart]!;
   if (!__internal.isBlockStart(first) && !/^\s*\|/.test(first)) return head.length;
   // heading / hr 单行完整，无需 holdback
   if (/^ {0,3}#{1,6}\s/.test(first)) return head.length;
   if (/^ {0,3}(?:(?:\*\s*){3,}|(?:-\s*){3,}|(?:_\s*){3,})$/.test(first)) return head.length;
   // 进度保护：holdback 块过大则放行，finalize 全量纠正
   if (end - chunkStart > 40) return head.length;
   return offsetOfLine(lines, chunkStart);
 }

 /**
  * 可延续行：列表项 / 引用 / 缩进续行 / 表格行。
  * 空行、fence 行、标题、分隔线、普通段落行都会终止回溯。
  */
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

function compact(nodes: MdNode[]): MdNode[] {
  return nodes.filter((n) => !(n.type === 'paragraph' && n.children.length === 0));
}

/** 合并相邻的 paragraph 节点（增量解析会把一段话切成多个 p） */
function mergeAdjacent(nodes: MdNode[]): MdNode[] {
  const out: MdNode[] = [];
  for (const n of nodes) {
    const prev = out[out.length - 1];
    if (prev && prev.type === 'paragraph' && n.type === 'paragraph') {
      out[out.length - 1] = {
        type: 'paragraph',
        children: [...prev.children, { type: 'br' }, ...n.children],
      };
    } else {
      out.push(n);
    }
  }
  return out;
}

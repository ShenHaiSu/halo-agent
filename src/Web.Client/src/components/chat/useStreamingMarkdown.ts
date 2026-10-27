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

import { ref, watch } from 'vue';
import { parseMarkdown, type MdNode } from '@/components/chat/MarkdownRenderer';

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
      committed.value = head ? compact(parseMarkdown(head)) : [];
      pendingText.value = lastNl >= 0 ? text.slice(lastNl + 1) : text;
      lastCommittedLen = head.length;
      lastSource = text;
      return;
    }

    // 追加：只处理新出现的完整行
    const newPart = text.slice(lastCommittedLen);
    const lastNl = newPart.lastIndexOf('\n');
    if (lastNl < 0) {
      // 还没有新的完整行，只更新 pending
      pendingText.value = newPart;
      lastSource = text;
      return;
    }

    const completeNew = newPart.slice(0, lastNl + 1);
    const deltaNodes = parseMarkdown(completeNew);

    committed.value = mergeAdjacent(committed.value.concat(deltaNodes));

    pendingText.value = newPart.slice(lastNl + 1);
    lastCommittedLen += completeNew.length;
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

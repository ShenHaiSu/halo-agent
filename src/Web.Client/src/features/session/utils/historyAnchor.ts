/** need07：历史锚点纯函数 + 剪切板双通道（供 Timeline/store/单测复用）。 */
import type { UnifiedChatMessage } from '@shared/bridge/hostBridge';

export type HistoryTargetKind = 'user' | 'assistant';

export interface HistoryTarget {
  kind: HistoryTargetKind;
  id: string;
  text: string;
}

function roleOf(m: UnifiedChatMessage): string {
  return String((m as unknown as { role?: string }).role || '').toLowerCase();
}

/** assistant 锚展开尾：吞掉其后连续 tool*，返回尾索引（含）。 */
export function expandAssistantEnd(msgs: UnifiedChatMessage[], idx: number): number {
  if (idx < 0 || idx >= msgs.length) return idx;
  if (roleOf(msgs[idx]) !== 'assistant') return idx;
  let t = idx + 1;
  while (t < msgs.length && roleOf(msgs[t]) === 'tool') t++;
  return t - 1;
}

/** 向前找第一条 user，找不到返回 -1。 */
export function findPrevUserIndex(msgs: UnifiedChatMessage[], fromIdx: number): number {
  let j = fromIdx;
  if (j >= msgs.length) j = msgs.length - 1;
  // assistant 情形从自身开始（若锚即 user，前调方传 idx-1）
  while (j >= 0 && roleOf(msgs[j]) !== 'user') j--;
  return j;
}

/** CefSharp Chrome109 双通道复制：Clipboard API 优先，失败降级 execCommand。 */
export async function copyTextToClipboard(text: string): Promise<boolean> {
  if (!text) return false;
  try {
    await navigator.clipboard.writeText(text);
    return true;
  } catch {
    try {
      const ta = document.createElement('textarea');
      ta.value = text;
      ta.style.position = 'fixed';
      ta.style.opacity = '0';
      document.body.appendChild(ta);
      ta.select();
      const ok = document.execCommand('copy');
      ta.remove();
      return ok;
    } catch {
      return false;
    }
  }
}

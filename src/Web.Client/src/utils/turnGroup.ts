/** need04-04：轮次聚合纯函数（assistant + 后续 tool* 归为一组，不改落盘结构）。 */
import type { UnifiedChatMessage, ChatImageRef } from '@/bridge/hostBridge';

export interface TurnToolRef { id: string; name: string; }
export interface TurnGroup {
  key: string;
  assistantId: string;
  reasoning: string | null;
  content: string | null;
  toolCalls: TurnToolRef[];
  toolMessages: UnifiedChatMessage[];
  telemetry: { inputTokens: number | null; cachedTokens: number | null; outputTokens: number | null } | null;
  createdAtUtc: string;
}

function toolRefsOf(msg: UnifiedChatMessage): TurnToolRef[] {
  const list = (msg as unknown as { toolCalls?: Array<{ id?: string; function?: { name?: string }; functionName?: string; name?: string }> }).toolCalls;
  if (!Array.isArray(list)) return [];
  return list.map((tc) => ({
    id: String((tc as { id?: string }).id || ''),
    name: String(tc.function?.name || tc.functionName || tc.name || 'tool'),
  })).filter((t) => t.id);
}

/** need09-05：消息图片取用（缺字段/null/空 = 纯文本；图片不拆轮，只跟随所属消息） */
export function imagesOf(msg: UnifiedChatMessage | null | undefined): ChatImageRef[] {
  if (!msg) return [];
  const imgs = (msg as UnifiedChatMessage).images;
  return Array.isArray(imgs) ? imgs : [];
}

/** need09-05：images 参与归并键——图片只跟随所属消息，不另起轮次；
 *  归并键 = 归属消息 id + 图片 sha16 指纹（同 assistant+tool 对逻辑不变）。 */
export function mergeKeyOf(msg: UnifiedChatMessage): string {
  const base = String(msg.id || '');
  const imgs = imagesOf(msg);
  if (imgs.length === 0) return base;
  return `${base}#img:${imgs.map((i) => i.sha16 || i.imageId).join(',')}`;
}

export function buildTurnGroups(messages: UnifiedChatMessage[]): TurnGroup[] {
  const groups: TurnGroup[] = [];
  let cur: TurnGroup | null = null;
  const flush = () => { if (cur) groups.push(cur); cur = null; };
  for (const msg of messages) {
    const role = String((msg as UnifiedChatMessage).role || '').toLowerCase();
    if (role === 'assistant') {
      flush();
      cur = {
        key: String(msg.id),
        assistantId: String(msg.id),
        reasoning: (msg.reasoningContent as string) || null,
        content: (msg.content as string) || null,
        toolCalls: toolRefsOf(msg),
        toolMessages: [],
        telemetry: (msg.inputTokens != null || msg.outputTokens != null)
          ? { inputTokens: (msg.inputTokens as number) ?? null, cachedTokens: (msg.cachedTokens as number) ?? null, outputTokens: (msg.outputTokens as number) ?? null }
          : null,
        createdAtUtc: String((msg as unknown as { createdAtUtc?: string }).createdAtUtc || ''),
      };
    } else if (role === 'tool') {
      const tid = String((msg as unknown as { toolCallId?: string }).toolCallId || '');
      if (cur && (cur.toolCalls.some((t) => t.id === tid) || !tid)) {
        cur.toolMessages.push(msg);
      } else if (cur && tid) {
        // toolCallId 不命中但紧邻：仍归入最近组（兼容老数据；图片跟随 tool 消息）
        cur.toolMessages.push(msg);
      } else {
        flush();
        // 孤立 tool：自成一组（无 assistant 头，仅工具舱）
        cur = {
          key: `tool-${String(msg.id)}`, assistantId: '', reasoning: null, content: null,
          toolCalls: [], toolMessages: [msg], telemetry: null,
          createdAtUtc: String((msg as unknown as { createdAtUtc?: string }).createdAtUtc || ''),
        };
        flush();
      }
    } else {
      flush();
    }
  }
  flush();
  return groups;
}

/** need09-05：会话导出 markdown（含图：`![file](relPath)`；丢失：`[图片已丢失：file]`；不嵌 base64） */
export function exportSessionMarkdown(messages: UnifiedChatMessage[]): string {
  const lines: string[] = [];
  for (const msg of messages) {
    const role = String(msg.role || '').toLowerCase();
    const head = role === 'user' ? '## 用户' : role === 'assistant' ? '## 助手' : role === 'tool' ? `## 工具${msg.name ? `（${msg.name}）` : ''}` : '## 系统';
    lines.push(head, '');
    if (msg.content) lines.push(String(msg.content), '');
    for (const img of imagesOf(msg)) {
      if ((img as ChatImageRef).lost) lines.push(`[图片已丢失：${img.fileName}]`, '');
      else lines.push(`![${img.fileName}](${img.relPath})`, '');
    }
  }
  return lines.join('\n');
}

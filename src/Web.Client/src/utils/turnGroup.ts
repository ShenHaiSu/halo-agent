/** need04-04：轮次聚合纯函数（assistant + 后续 tool* 归为一组，不改落盘结构）。 */
import type { UnifiedChatMessage } from '@/bridge/hostBridge';

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
        // toolCallId 不命中但紧邻：仍归入最近组（兼容老数据）
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

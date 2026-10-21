/** need04-04：轮次聚合纯函数（assistant + 后续 tool* 归为一组，不改落盘结构）。 */
import type { UnifiedChatMessage, ChatImageRef } from '@shared/bridge/hostBridge';

export interface TurnToolRef {
  id: string;
  name: string;
}
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
  const list = (
    msg as unknown as { toolCalls?: Array<{ id?: string; function?: { name?: string }; functionName?: string; name?: string }> }
  ).toolCalls;
  if (!Array.isArray(list)) return [];
  return list
    .map((tc) => ({
      id: String((tc as { id?: string }).id || ''),
      name: String(tc.function?.name || tc.functionName || tc.name || 'tool'),
    }))
    .filter((t) => t.id);
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

/** need19：时间轴条目（主时间轴与目录共用；key 即锚点 key）。 */
export interface TimelineEntry {
  key: string;
  kind: 'user' | 'system' | 'turn';
  msg?: UnifiedChatMessage;
  group?: TurnGroup;
}

/**
 * need19：由 messages + 可见 turn 组构造时间轴条目。
 * 与 MessageTimeline.vue 原内联 timelineItems 逻辑逐项一致（纯抽取，不改语义）。
 */
export function buildTimelineEntries(messages: UnifiedChatMessage[], groups: TurnGroup[]): TimelineEntry[] {
  const items: TimelineEntry[] = [];
  const groupsByAssistant = new Map<string, TurnGroup>();
  for (const g of groups) {
    if (g.assistantId) groupsByAssistant.set(g.assistantId, g);
    else if (g.toolMessages.length) {
      items.push({ key: g.key, kind: 'turn', group: g });
    }
  }
  const emitted = new Set<string>();
  for (const msg of messages) {
    const role = String(msg.role || '').toLowerCase();
    if (role === 'user') items.push({ key: `u-${msg.id}`, kind: 'user', msg });
    else if (role !== 'assistant' && role !== 'tool') items.push({ key: `s-${msg.id}`, kind: 'system', msg });
    else if (role === 'assistant') {
      const g = groupsByAssistant.get(String(msg.id));
      if (g && !emitted.has(g.key)) {
        emitted.add(g.key);
        items.push({ key: g.key, kind: 'turn', group: g });
      }
    }
  }
  for (const g of groups) {
    if (g.assistantId && !emitted.has(g.key)) items.push({ key: g.key, kind: 'turn', group: g });
  }
  return items;
}

export function buildTurnGroups(messages: UnifiedChatMessage[]): TurnGroup[] {
  const groups: TurnGroup[] = [];
  let cur: TurnGroup | null = null;
  const flush = () => {
    if (cur) groups.push(cur);
    cur = null;
  };
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
        telemetry:
          msg.inputTokens != null || msg.outputTokens != null
            ? {
                inputTokens: (msg.inputTokens as number) ?? null,
                cachedTokens: (msg.cachedTokens as number) ?? null,
                outputTokens: (msg.outputTokens as number) ?? null,
              }
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
          key: `tool-${String(msg.id)}`,
          assistantId: '',
          reasoning: null,
          content: null,
          toolCalls: [],
          toolMessages: [msg],
          telemetry: null,
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

/** need22-P3 G1：增量归并——仅末 turn 重算，其余复用引用（依赖行表 rowId=msg.id 稳定）。
 * 历史删改（消息减少/末 assistant 落在旧前缀内且后缀非纯 tool）时全量回退，保证正确性。 */
export function buildTurnGroupsIncremental(
  prevGroups: TurnGroup[],
  prevMsgCount: number,
  messages: UnifiedChatMessage[]
): { groups: TurnGroup[]; msgCount: number } {
  const full = (): { groups: TurnGroup[]; msgCount: number } => ({ groups: buildTurnGroups(messages), msgCount: messages.length });
  if (!prevGroups.length || prevMsgCount <= 0 || messages.length < prevMsgCount) return full();
  let lastAsstIdx = -1;
  for (let i = messages.length - 1; i >= 0; i--) {
    if (String((messages[i] as UnifiedChatMessage).role || '').toLowerCase() === 'assistant') {
      lastAsstIdx = i;
      break;
    }
  }
  if (lastAsstIdx < 0) return full();
  if (lastAsstIdx < prevMsgCount) {
    let onlyTools = true;
    for (let i = prevMsgCount; i < messages.length; i++) {
      if (String((messages[i] as UnifiedChatMessage).role || '').toLowerCase() !== 'tool') {
        onlyTools = false;
        break;
      }
    }
    if (!onlyTools) return full();
  }
  const lastAsstId = String(messages[lastAsstIdx].id || '');
  const keepEnd = prevGroups.findIndex((g) => g.assistantId === lastAsstId);
  const kept = keepEnd < 0 ? [] : prevGroups.slice(0, keepEnd);
  const tail = buildTurnGroups(messages.slice(lastAsstIdx));
  return { groups: [...kept, ...tail], msgCount: messages.length };
}

/** need22-P3 G1：blockHash（终态 md.render 缓存键；FNV-1a 32bit hex + 长度）。 */
export function blockHash(s: string): string {
  let h = 0x811c9dc5;
  const str = String(s || '');
  for (let i = 0; i < str.length; i++) {
    h ^= str.charCodeAt(i);
    h = Math.imul(h, 0x01000193);
  }
  return (h >>> 0).toString(16).padStart(8, '0') + ':' + str.length;
}

/** need09-05：会话导出 markdown（含图：`![file](relPath)`；丢失：`[图片已丢失：file]`；不嵌 base64） */
export function exportSessionMarkdown(messages: UnifiedChatMessage[]): string {
  const lines: string[] = [];
  for (const msg of messages) {
    const role = String(msg.role || '').toLowerCase();
    const head =
      role === 'user'
        ? '## 用户'
        : role === 'assistant'
          ? '## 助手'
          : role === 'tool'
            ? `## 工具${msg.name ? `（${msg.name}）` : ''}`
            : '## 系统';
    lines.push(head, '');
    if (msg.content) lines.push(String(msg.content), '');
    for (const img of imagesOf(msg)) {
      if ((img as ChatImageRef).lost) lines.push(`[图片已丢失：${img.fileName}]`, '');
      else lines.push(`![${img.fileName}](${img.relPath})`, '');
    }
  }
  return lines.join('\n');
}

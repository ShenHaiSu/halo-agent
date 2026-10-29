// src/Web.Client/src/stores/messages.ts
// P5 扩展：streamingId / lastSeq / injected / truncated。
// P08-fix 02：新增 normalizeToolCalls 唯一适配（args/output 契约消化 + 双写），过滤孤立 role=tool。
import { defineStore } from 'pinia';
import { computed, ref } from 'vue';
import { transport } from '@/transport';

export type MessageRole = 'user' | 'assistant' | 'tool' | 'system';

export interface ToolCallInfo {
  id: string;
  name: string;
  arguments: string;
  result?: string;
  ok?: boolean;
  status: 'pending' | 'running' | 'awaiting-user' | 'done' | 'error' | 'cancelled';
  output?: string;
  alertLevel?: number;
  startedAt?: number;
  durationMs?: number;
}

export interface MessageItem {
  id: string;
  role: MessageRole;
  content: string;
  reasoning?: string;
  toolCalls?: ToolCallInfo[];
  toolCallId?: string;
  ts: number;
  streaming?: boolean;
  truncatedByLength?: boolean;
  truncatedByUser?: boolean;
  injected?: boolean;
}

/**
 * P08-fix 02 唯一适配函数：消化后端 ToolCallView{args,output} 与前端 ToolCallInfo{arguments,result} 的字段分歧。
 * - args ?? arguments → arguments（缺省 '{}'，保证 ToolCardBody 的 JSON.parse 与 summarizeArgs 不空）
 * - output → output + result 双写（result ?? output，保证历史卡片结果区有内容）
 * - ok 透传（boolean 才保留，否则 undefined，deriveState 才能正确判 error/done）
 * - status 恒 'done'，alertLevel 缺省 0
 */
export function normalizeToolCalls(raw: unknown): ToolCallInfo[] | undefined {
  if (!Array.isArray(raw)) return undefined;
  return (raw as Array<Record<string, unknown>>).map((tc) => {
    const o = (tc ?? {}) as Record<string, unknown>;
    let args: string;
    if (typeof o.args === 'string') args = o.args;
    else if (typeof o.arguments === 'string') args = o.arguments;
    else if (o.args != null && typeof o.args === 'object') {
      try {
        args = JSON.stringify(o.args);
      } catch {
        args = '{}';
      }
    } else if (o.arguments != null && typeof o.arguments === 'object') {
      try {
        args = JSON.stringify(o.arguments);
      } catch {
        args = '{}';
      }
    } else {
      args = '{}';
    }
    if (!args) args = '{}';
    const output =
      typeof o.output === 'string' ? o.output : typeof o.result === 'string' ? (o.result as string) : undefined;
    return {
      id: String(o.id ?? ''),
      name: String(o.name ?? ''),
      arguments: args,
      result: output,
      ok: typeof o.ok === 'boolean' ? (o.ok as boolean) : undefined,
      status: 'done' as const,
      output,
      alertLevel: typeof o.alertLevel === 'number' ? (o.alertLevel as number) : 0,
    };
  });
}

export const useMessagesStore = defineStore('messages', () => {
  const items = ref<MessageItem[]>([]);
  const lastSeq = ref(0);
  const loading = ref(false);
  const streamingId = ref<string | null>(null);

  let idCounter = 0;
  function nextId(prefix: string): string {
    return `${prefix}-${Date.now().toString(36)}-${(++idCounter).toString(36)}`;
  }

  const isEmpty = computed(() => items.value.length === 0);
  const lastAssistant = computed(() =>
    [...items.value].reverse().find((m) => m.role === 'assistant')
  );

  function setLastSeq(seq: number): void {
    if (seq > lastSeq.value) lastSeq.value = seq;
  }

  // P09-fix2 A：游标只增不减。快照无 seq（契约冻结）时保持原游标，禁止回退为 0，
  // 否则下一 tick pollEvents(0) 会重放旧事件覆盖新快照（切换闪白主因）。
  function applySnapshot(snapshot: {
    seq?: number;
    messages?: Array<Record<string, unknown>>;
  }): void {
    if (typeof snapshot.seq === 'number') setLastSeq(snapshot.seq);
    const raw = Array.isArray(snapshot.messages) ? snapshot.messages : [];
    // P08-fix 02：快照中残留的孤立 role=tool 消息直接过滤，不再渲染为正文。
    const filtered = raw.filter((m) => String((m as Record<string, unknown>)?.role ?? '') !== 'tool');
    items.value = filtered.map(normalizeMessage);
    streamingId.value = null;
  }

  function normalizeMessage(m: Record<string, unknown>): MessageItem {
    const role = String(m.role ?? 'user') as MessageRole;
    const content =
      typeof m.content === 'string'
        ? m.content
        : typeof m.text === 'string'
          ? String(m.text)
          : Array.isArray(m.content)
            ? (m.content as Array<{ text?: string }>).map((p) => p.text ?? '').join('')
            : '';
    const toolCalls = Array.isArray(m.tool_calls)
      ? (m.tool_calls as Array<Record<string, unknown>>).map((tc) => {
          const fn = (tc.function ?? {}) as Record<string, unknown>;
          return {
            id: String(tc.id ?? ''),
            name: String(fn.name ?? ''),
            arguments:
              typeof fn.arguments === 'string' ? fn.arguments : JSON.stringify(fn.arguments ?? {}),
            status: 'done' as const,
          };
        })
      : normalizeToolCalls(m.toolCalls);

    return {
      id: typeof m.id === 'string' ? m.id : nextId(role),
      role,
      content,
      reasoning: typeof m.reasoning === 'string' ? m.reasoning : undefined,
      toolCalls,
      toolCallId: typeof m.tool_call_id === 'string' ? m.tool_call_id : undefined,
      ts:
        typeof m.ts === 'number'
          ? m.ts
          : typeof m.tsMs === 'number'
            ? (m.tsMs as number)
            : Date.now(),
      injected: m.injected === true,
    };
  }

  function append(msg: Omit<MessageItem, 'id' | 'ts'> & { id?: string; ts?: number }): string {
    const id = msg.id ?? nextId(msg.role);
    items.value.push({ ...msg, id, ts: msg.ts ?? Date.now() } as MessageItem);
    return id;
  }

  function patch(id: string, p: Partial<MessageItem>): void {
    const target = items.value.find((m) => m.id === id);
    if (!target) return;
    Object.assign(target, p);
  }

  function appendDelta(id: string, text: string): void {
    const target = items.value.find((m) => m.id === id);
    if (!target) return;
    target.content += text;
    target.streaming = true;
  }

  function appendReasoning(id: string, text: string): void {
    const target = items.value.find((m) => m.id === id);
    if (!target) return;
    target.reasoning = (target.reasoning ?? '') + text;
  }

  function ensureToolCall(messageId: string, toolCallId: string, name: string, args: string): void {
    const msg = items.value.find((m) => m.id === messageId);
    if (!msg) return;
    msg.toolCalls ??= [];
    if (!msg.toolCalls.some((t) => t.id === toolCallId)) {
      msg.toolCalls.push({
        id: toolCallId,
        name,
        arguments: args,
        status: 'running',
        output: '',
        alertLevel: 0,
        startedAt: Date.now(),
      });
    }
  }

  function patchToolCall(messageId: string, toolCallId: string, p: Partial<ToolCallInfo>): void {
    const msg = items.value.find((m) => m.id === messageId);
    if (!msg?.toolCalls) return;
    const tc = msg.toolCalls.find((t) => t.id === toolCallId);
    if (!tc) return;
    Object.assign(tc, p);
  }

  async function loadSnapshot(): Promise<void> {
    loading.value = true;
    try {
      const snap = await transport.request<{
        seq?: number;
        messages?: Array<Record<string, unknown>>;
      }>('state.snapshot', null);
      applySnapshot(snap ?? {});
    } finally {
      loading.value = false;
    }
  }

  // P09-fix2 A：clear 只清条目不碰游标（游标只增不减）；真正断连/登出再用 resetCursor。
  function clearItems(): void {
    items.value = [];
    streamingId.value = null;
  }

  function resetCursor(): void {
    lastSeq.value = 0;
  }

  function clear(): void {
    clearItems();
  }

  return {
    items,
    lastSeq,
    loading,
    streamingId,
    isEmpty,
    lastAssistant,
    applySnapshot,
    clearItems,
    resetCursor,
    append,
    patch,
    appendDelta,
    appendReasoning,
    ensureToolCall,
    patchToolCall,
    loadSnapshot,
    clear,
    nextId,
    setLastSeq,
  };
});

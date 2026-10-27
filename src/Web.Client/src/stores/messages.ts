// src/Web.Client/src/stores/messages.ts
// P5 扩展：streamingId / lastSeq / injected / truncated。
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

  function applySnapshot(snapshot: {
    seq?: number;
    messages?: Array<Record<string, unknown>>;
  }): void {
    lastSeq.value = typeof snapshot.seq === 'number' ? snapshot.seq : 0;
    const raw = Array.isArray(snapshot.messages) ? snapshot.messages : [];
    items.value = raw.map(normalizeMessage);
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
      : Array.isArray(m.toolCalls)
        ? (m.toolCalls as Array<Record<string, unknown>>).map((tc) => ({
            id: String(tc.id ?? ''),
            name: String(tc.name ?? ''),
            arguments:
              typeof tc.arguments === 'string' ? tc.arguments : JSON.stringify(tc.arguments ?? {}),
            result: typeof tc.result === 'string' ? tc.result : undefined,
            ok: typeof tc.ok === 'boolean' ? tc.ok : undefined,
            status: String(tc.status ?? 'done') as ToolCallInfo['status'],
            output: typeof tc.output === 'string' ? tc.output : undefined,
            alertLevel: typeof tc.alertLevel === 'number' ? tc.alertLevel : 0,
          }))
        : undefined;

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

  function clear(): void {
    items.value = [];
    lastSeq.value = 0;
    streamingId.value = null;
  }

  return {
    items,
    lastSeq,
    loading,
    streamingId,
    isEmpty,
    lastAssistant,
    applySnapshot,
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

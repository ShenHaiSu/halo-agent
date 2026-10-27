// src/Web.Client/src/stores/messages.ts
// D4-8 消息列表 store（最关键的一个）。
import { defineStore } from 'pinia';
import { computed, ref } from 'vue';
import { transport } from '@/transport';

export type MessageRole = 'user' | 'assistant' | 'tool' | 'system';

export interface ToolCallInfo {
  id: string;
  name: string;
  arguments: string;
  /** 执行结果 */
  result?: string;
  ok?: boolean;
  /** 执行状态 */
  status: 'pending' | 'running' | 'done' | 'error' | 'cancelled';
  /** 实时输出（shell 工具） */
  output?: string;
  /** 静默预警级别 0/1/2 */
  alertLevel?: number;
}

export interface MessageItem {
  /** 稳定唯一 id（用于 :key 与滚动定位） */
  id: string;
  role: MessageRole;
  content: string;
  /** 思考内容（reasoning_content） */
  reasoning?: string;
  toolCalls?: ToolCallInfo[];
  /** tool 消息对应的调用 id */
  toolCallId?: string;
  /** 时间戳（ms） */
  ts: number;
  /** 是否仍在流式生成 */
  streaming?: boolean;
  /** 被长度截断 */
  truncatedByLength?: boolean;
  /** 被用户中断 */
  truncatedByUser?: boolean;
}

/**
 * 消息列表 store。
 *
 * ============ 三条核心不变式 ============
 *  I-1：流式生成时，`assistant` 消息的 id 在整个回合内保持不变
 *       （否则每次 delta 都新建一条消息，列表会爆炸）
 *  I-2：`lastSeq` 只增不减（事件去重与断线重连的依据）
 *  I-3：`applySnapshot` 是唯一能"重置"列表的入口
 *       （其他地方只能 append / patch）
 * ============ 为什么 key 用 id 而不是 index ============
 *  用 index 做 key 时，插入一条消息会导致后面所有消息的 DOM 重建，
 *  滚动位置与输入法状态全部丢失。
 * ============ ============
 */
export const useMessagesStore = defineStore('messages', () => {
  const items = ref<MessageItem[]>([]);
  const lastSeq = ref(0);
  const loading = ref(false);

  let idCounter = 0;
  function nextId(prefix: string): string {
    return `${prefix}-${Date.now().toString(36)}-${(++idCounter).toString(36)}`;
  }

  const isEmpty = computed(() => items.value.length === 0);
  const lastAssistant = computed(() =>
    [...items.value].reverse().find((m) => m.role === 'assistant')
  );

  /** 从快照全量重置（唯一入口） */
  function applySnapshot(snapshot: { seq?: number; messages?: Array<Record<string, unknown>> }) {
    lastSeq.value = typeof snapshot.seq === 'number' ? snapshot.seq : 0;
    const raw = Array.isArray(snapshot.messages) ? snapshot.messages : [];
    items.value = raw.map(normalizeMessage);
  }

  function normalizeMessage(m: Record<string, unknown>): MessageItem {
    const role = String(m.role ?? 'user') as MessageRole;
    const content =
      typeof m.content === 'string'
        ? m.content
        : typeof (m as { text?: unknown }).text === 'string'
          ? String((m as { text?: unknown }).text)
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
      : undefined;

    return {
      id: typeof m.id === 'string' ? m.id : nextId(role),
      role,
      content,
      reasoning: typeof m.reasoning === 'string' ? m.reasoning : undefined,
      toolCalls,
      toolCallId: typeof m.tool_call_id === 'string' ? m.tool_call_id : undefined,
      ts: typeof m.ts === 'number' ? m.ts : Date.now(),
    };
  }

  /** 追加一条消息，返回其 id */
  function append(msg: Omit<MessageItem, 'id' | 'ts'> & { id?: string; ts?: number }): string {
    const id = msg.id ?? nextId(msg.role);
    items.value.push({
      ...msg,
      id,
      ts: msg.ts ?? Date.now(),
    } as MessageItem);
    return id;
  }

  /** 按 id 局部更新 */
  function patch(id: string, patch: Partial<MessageItem>): void {
    const target = items.value.find((m) => m.id === id);
    if (!target) return;
    Object.assign(target, patch);
  }

  /** 追加流式文本（不改变 id） */
  function appendDelta(id: string, text: string): void {
    const target = items.value.find((m) => m.id === id);
    if (!target) return;
    target.content += text;
    target.streaming = true;
  }

  /** 追加思考内容 */
  function appendReasoning(id: string, text: string): void {
    const target = items.value.find((m) => m.id === id);
    if (!target) return;
    target.reasoning = (target.reasoning ?? '') + text;
  }

  /** 更新某个 toolCall 的状态 */
  function patchToolCall(
    messageId: string,
    toolCallId: string,
    patch: Partial<ToolCallInfo>
  ): void {
    const msg = items.value.find((m) => m.id === messageId);
    if (!msg?.toolCalls) return;
    const tc = msg.toolCalls.find((t) => t.id === toolCallId);
    if (!tc) return;
    Object.assign(tc, patch);
  }

  /** 加载快照 */
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

  /** 清空（新会话） */
  function clear(): void {
    items.value = [];
    lastSeq.value = 0;
  }

  return {
    items,
    lastSeq,
    loading,
    isEmpty,
    lastAssistant,

    applySnapshot,
    append,
    patch,
    appendDelta,
    appendReasoning,
    patchToolCall,
    loadSnapshot,
    clear,
    nextId,
  };
});

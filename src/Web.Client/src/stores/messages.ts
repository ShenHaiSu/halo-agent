// src/Web.Client/src/stores/messages.ts
// P15 TurnCard 三段式：turns 为唯一真相，drafts 按 (turnId:used,kind) 分槽。
import { defineStore } from 'pinia';
import { computed, ref } from 'vue';
import { transport } from '@/transport';
 import { useSessionStore } from '@/stores/session';
 import { useStreamStore } from './stream';

export type MessageRole = 'user' | 'assistant' | 'tool' | 'system';

export type ToolCallKind = 'function' | 'unknown';
export type ReasoningKind = 'full' | 'summary';

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
  kind?: ToolCallKind;
  rawType?: string | null;
  payload?: Record<string, unknown> | null;
  used?: number;
  argsTotal?: number;
  outputTotal?: number;
  truncated?: boolean;
}

export interface MessageItem {
  id: string;
  role: MessageRole;
  content: string;
  reasoning?: string;
  reasoningKind?: ReasoningKind;
  responseId?: string | null;
  toolCalls?: ToolCallInfo[];
  toolCallId?: string;
  ts: number;
  streaming?: boolean;
  truncatedByLength?: boolean;
  truncatedByUser?: boolean;
  injected?: boolean;
  ord?: number;
}

export interface TurnView {
  turnId: string;
  user: MessageItem;
  injected: MessageItem[];
  process: MessageItem[];
  final: MessageItem;
}

export interface Drafts {
  text: string;
  reasoning: string;
}
export type ProcessOpenMode = 'auto' | 'userOpen' | 'userClosed';

/**
 * P08-fix 02 唯一适配函数（保留）：消化后端 ToolCallView{args/output} 与前端 ToolCallInfo{arguments,result} 的字段分歧。
 */
export function normalizeToolCalls(raw: unknown): ToolCallInfo[] | undefined {
  if (!Array.isArray(raw)) return undefined;
  return (raw as Array<Record<string, unknown>>).map((tc) => {
    const o = (tc ?? {}) as Record<string, unknown>;
    let args: string;
    if (typeof o.args === 'string') args = o.args;
    else if (typeof o.arguments === 'string') args = o.arguments;
    else if (o.args !== null && o.args !== undefined && typeof o.args === 'object') {
      try {
        args = JSON.stringify(o.args);
      } catch {
        args = '{}';
      }
    } else if (o.arguments !== null && o.arguments !== undefined && typeof o.arguments === 'object') {
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
      kind: o.kind === 'unknown' ? 'unknown' : 'function',
      rawType: typeof o.rawType === 'string' ? (o.rawType as string) : null,
      payload:
        o.payload !== null && typeof o.payload === 'object' && !Array.isArray(o.payload)
          ? (o.payload as Record<string, unknown>)
          : null,
      used: typeof o.used === 'number' ? (o.used as number) : undefined,
      argsTotal: typeof o.argsTotal === 'number' ? (o.argsTotal as number) : args.length,
      outputTotal: typeof o.outputTotal === 'number' ? (o.outputTotal as number) : (output?.length ?? 0),
      truncated: o.truncated === true,
    };
  });
}

// P11-C：OpenAI 历史格式 tool_calls 结果回填（保留，供旧数据回填用）。
export function buildToolContentMap(raw: Array<Record<string, unknown>>): Map<string, string> {
  const map = new Map<string, string>();
  for (const m of raw) {
    if (String(m?.role ?? '') !== 'tool') continue;
    const id = m?.tool_call_id;
    if (typeof id !== 'string' || !id || map.has(id)) continue;
    const c = m?.content;
    if (typeof c === 'string') map.set(id, c);
    else if (typeof m?.text === 'string') map.set(id, String(m.text));
    else if (Array.isArray(c))
      map.set(id, (c as Array<{ text?: string }>).map((p) => p.text ?? '').join(''));
  }
  return map;
}

export function normalizeOpenAiToolCalls(raw: unknown, toolContentById?: Map<string, string>): ToolCallInfo[] {
  if (!Array.isArray(raw)) return [];
  return (raw as Array<Record<string, unknown>>).map((tc) => {
    const fn = ((tc as Record<string, unknown>)?.function ?? {}) as Record<string, unknown>;
    const id = String((tc as Record<string, unknown>)?.id ?? '');
    const output = toolContentById?.get(id);
    return {
      id,
      name: String(fn.name ?? ''),
      arguments: typeof fn.arguments === 'string' ? fn.arguments : JSON.stringify(fn.arguments ?? {}),
      result: output,
      ok: undefined,
      status: 'done' as const,
      output,
      kind: 'function' as const,
    };
  });
}

function normalizeMessage(m: Record<string, unknown>, toolContentById?: Map<string, string>): MessageItem {
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
    ? normalizeOpenAiToolCalls(m.tool_calls, toolContentById)
    : normalizeToolCalls(m.toolCalls);
  return {
    id: typeof m.id === 'string' ? m.id : `m-${Date.now().toString(36)}`,
    role,
    content,
    reasoning: typeof m.reasoning === 'string' ? m.reasoning : undefined,
    reasoningKind: m.reasoningKind === 'summary' ? 'summary' : 'full',
    responseId: typeof m.responseId === 'string' ? (m.responseId as string) : undefined,
    toolCalls,
    toolCallId: typeof m.tool_call_id === 'string' ? m.tool_call_id : undefined,
    ts:
      typeof m.ts === 'number'
        ? m.ts
        : typeof m.tsMs === 'number'
          ? (m.tsMs as number)
          : Date.now(),
    injected: m.injected === true,
    ord: typeof m.ord === 'number' ? (m.ord as number) : 0,
  };
}

function normalizeTurn(t: Record<string, unknown>): TurnView {
  const turnId = typeof t.turnId === 'string' ? t.turnId : `t-hist-${Math.random().toString(36).slice(2, 8)}`;
  const toolMap = buildToolContentMap([]);
  const user = t.user && typeof t.user === 'object' ? normalizeMessage(t.user as Record<string, unknown>, toolMap) : { id: `m-${turnId}-u`, role: 'user' as const, content: '', ts: Date.now(), ord: 0 };
  const injected = Array.isArray(t.injected) ? (t.injected as Array<Record<string, unknown>>).map((m) => normalizeMessage(m, toolMap)) : [];
  const process = Array.isArray(t.process) ? (t.process as Array<Record<string, unknown>>).map((m) => normalizeMessage(m, toolMap)) : [];
  const final = t.final && typeof t.final === 'object' ? normalizeMessage(t.final as Record<string, unknown>, toolMap) : { id: `m-${turnId}-f`, role: 'assistant' as const, content: '', ts: Date.now(), ord: 0 };
  return { turnId, user, injected, process, final };
}

/** 旧扁平 messages → TurnView[] 回填（不写回文件，只重排内存）。 */
export function projectTurnsFromFlat(raw: Array<Record<string, unknown>>): TurnView[] {
  const toolContentById = buildToolContentMap(raw);
  const filtered = raw.filter((m) => String((m as Record<string, unknown>)?.role ?? '') !== 'tool');
  const items = filtered.map((m) => normalizeMessage(m as Record<string, unknown>, toolContentById));
  const turns: TurnView[] = [];
  let cur: TurnView | null = null;
  let seq = 0;
  const flush = () => { if (cur) { turns.push(cur); cur = null; } };
  for (const it of items) {
    if (it.role === 'user') {
      if (!cur || (cur.user.content !== '' || cur.process.length > 0 || cur.final.content !== '' || cur.injected.length > 0) && cur.user.id !== '') {
        // 新回合：若当前已有 user 则 flush
        if (cur) flush();
        seq++;
        cur = { turnId: `t-hist-${seq}`, user: it, injected: [], process: [], final: { id: `m-hist-${seq}-f`, role: 'assistant', content: '', ts: it.ts, ord: 0 } };
      } else if (cur && cur.user.content === '') {
        cur.user = it;
      } else if (cur) {
        cur.injected.push({ ...it, injected: true });
      }
    } else if (it.role === 'assistant') {
      if (!cur) {
        seq++;
        cur = { turnId: `t-hist-${seq}`, user: { id: `m-hist-${seq}-u`, role: 'user', content: '', ts: it.ts, ord: 0 }, injected: [], process: [], final: it };
      } else if (cur.final.content === '' && (cur.final.toolCalls ?? []).length === 0) {
        cur.final = it;
      } else {
        // 前一个 final 降为 process
        cur.process.push(cur.final);
        cur.final = it;
      }
    }
  }
  flush();
  return turns;
}

export const useMessagesStore = defineStore('messages', () => {
  const turns = ref<TurnView[]>([]);
  const drafts = ref<Record<string, Drafts>>({});
  const processOpen = ref<Record<string, ProcessOpenMode>>({});
  const toolTail = ref<Map<string, { argsRest?: string; outputRest?: string; loading?: boolean; error?: string | null }>>(new Map());
  const lastSeq = ref(0);
  const loading = ref(false);
  const mismatch = ref(0);

  let idCounter = 0;
  function nextId(prefix: string): string {
    return `${prefix}-${Date.now().toString(36)}-${(++idCounter).toString(36)}`;
  }

  const items = computed<MessageItem[]>(() => turns.value.flatMap((t) => [t.user, ...t.injected, ...t.process, t.final]).filter((m) => !(m.role === 'user' && m.content === '' && !m.injected)));
  const isEmpty = computed(() => turns.value.length === 0);
  const lastAssistant = computed(() => {
    const t = [...turns.value].reverse().find((x) => x.final.content);
    return t?.final;
  });

  function setLastSeq(seq: number): void {
    if (seq > lastSeq.value) lastSeq.value = seq;
  }

  function draftKey(turnId: string, used: number): string {
    return `${turnId}:${used}`;
  }

  function applySnapshot(snapshot: { seq?: number; turns?: unknown; messages?: Array<Record<string, unknown>> }): void {
    if (typeof snapshot.seq === 'number') setLastSeq(snapshot.seq);
    if (Array.isArray(snapshot.turns)) {
      turns.value = (snapshot.turns as Array<Record<string, unknown>>).map(normalizeTurn);
    } else if (Array.isArray(snapshot.messages)) {
      turns.value = projectTurnsFromFlat(snapshot.messages);
    } else {
      turns.value = [];
    }
    drafts.value = {};
    toolTail.value = new Map();
    // 快照替换后若 currentTurn 不在新 turns 中，视为旧会话残留（迟到帧点亮），强制收尾，避免历史按流式渲染。
    try {
      const cur = useStreamStore().currentTurn?.turnId;
      if (cur && !turns.value.some((t) => t.turnId === cur)) useStreamStore().endTurn();
    } catch {
      /* store 未挂载时忽略 */
    }
  }

  function findTurn(turnId: string): TurnView | undefined {
    return turns.value.find((t) => t.turnId === turnId);
  }

  function ensureTurn(turnId: string): TurnView {
    let t = findTurn(turnId);
    if (!t) {
      t = {
        turnId,
        user: { id: nextId('u'), role: 'user', content: '', ts: Date.now(), ord: 0 },
        injected: [],
        process: [],
        final: { id: nextId('a'), role: 'assistant', content: '', ts: Date.now(), ord: 0 },
      };
      turns.value.push(t);
    }
    return t;
  }

  function appendDraft(turnId: string, used: number, kind: 'text' | 'reasoning', chunk: string): void {
    if (used <= 0) {
      mismatch.value++;
      console.warn('[turncard] 非法 used=0，已计数不拆卡');
      return;
    }
    const k = draftKey(turnId, used);
    const d = drafts.value[k] ?? { text: '', reasoning: '' };
    if (kind === 'text') d.text += chunk;
    else d.reasoning += chunk;
    drafts.value[k] = d;
  }

  function clearDraft(turnId: string, used: number): void {
    delete drafts.value[draftKey(turnId, used)];
  }

  function clearTurnDrafts(turnId: string): void {
    for (const k of Object.keys(drafts.value)) {
      if (k.startsWith(`${turnId}:`)) delete drafts.value[k];
    }
  }

  /** round.ended{hasTools=true} → 把该轮草稿搬进 process 并 finalize，清草稿。 */
  function moveRoundToProcess(turnId: string, used: number): void {
    const t = ensureTurn(turnId);
    const k = draftKey(turnId, used);
    const d = drafts.value[k];
    if (d && (d.text || d.reasoning || (t.final.toolCalls ?? []).length > 0)) {
      // 若 final 已有工具卡（同 used），随草稿一起搬
      const tools = t.final.toolCalls ?? [];
      if (d.text || d.reasoning) {
        t.process.push({
          id: nextId('p'),
          role: 'assistant',
          content: d.text,
          reasoning: d.reasoning || undefined,
          ts: Date.now(),
          ord: used,
          toolCalls: tools.length > 0 ? [...tools] : undefined,
        });
        t.final = { id: nextId('a'), role: 'assistant', content: '', ts: Date.now(), ord: 0, toolCalls: undefined };
      } else if (tools.length > 0) {
        t.process.push({
          id: nextId('p'),
          role: 'assistant',
          content: '',
          ts: Date.now(),
          ord: used,
          toolCalls: [...tools],
        });
        t.final = { id: nextId('a'), role: 'assistant', content: '', ts: Date.now(), ord: 0, toolCalls: undefined };
      }
    }
    delete drafts.value[k];
  }

  /** turn.finished → 全量 parse 替换 final，折叠 process（auto），清 currentTurn 由 stream 侧处理。 */
  function finalizeTurn(turnId: string, finalUsed: number, text: string, truncated: boolean, responseId?: string): void {
    const t = ensureTurn(turnId);
    // O3：若 round.ended 缺失，以 finalUsed 重切（ord<finalUsed 进 process）
    for (const k of Object.keys(drafts.value)) {
      if (!k.startsWith(`${turnId}:`)) continue;
      const used = Number(k.split(':')[1] ?? '0');
      if (used < finalUsed) {
        moveRoundToProcess(turnId, used);
      } else if (used === finalUsed) {
        // 末轮草稿直接合入 final（稳态以 finished.text 为准）
        delete drafts.value[k];
      } else {
        delete drafts.value[k];
      }
    }
    t.final = {
      ...t.final,
      content: text,
      truncatedByLength: truncated,
      responseId: responseId ?? t.final.responseId,
      ord: finalUsed,
      streaming: false,
    };
    // auto 折叠：仅当 mode 为 auto（缺省）时由 TurnCard computed 体现；此处不强制写 userClosed
    if (!(turnId in processOpen.value)) processOpen.value[turnId] = 'auto';
  }

  function appendOptimisticUser(content: string): string {
    const turnId = `t-local-${Date.now().toString(36)}-${(++idCounter).toString(36)}`;
    turns.value.push({
      turnId,
      user: { id: nextId('u'), role: 'user', content, ts: Date.now(), ord: 0 },
      injected: [],
      process: [],
      final: { id: nextId('a'), role: 'assistant', content: '', ts: Date.now(), ord: 0, streaming: true },
    });
    return turnId;
  }

  function appendInjected(turnId: string, text: string): void {
    const t = ensureTurn(turnId);
    t.injected.push({ id: nextId('u'), role: 'user', content: text, ts: Date.now(), injected: true, ord: 0 });
  }

  function ensureToolCall(turnId: string, used: number, toolCallId: string, name: string, args: string, kind?: string, rawType?: string): void {
    const t = ensureTurn(turnId);
    t.final.toolCalls ??= [];
    if (!t.final.toolCalls.some((x) => x.id === toolCallId)) {
      t.final.toolCalls.push({
        id: toolCallId,
        name,
        arguments: args,
        status: 'running',
        output: '',
        alertLevel: 0,
        startedAt: Date.now(),
        kind: kind === 'unknown' ? 'unknown' : 'function',
        rawType: typeof rawType === 'string' ? rawType : null,
        payload: null,
        used,
        argsTotal: args.length,
        outputTotal: 0,
        truncated: false,
      });
    }
  }

  function patchToolCall(turnId: string, toolCallId: string, p: Partial<ToolCallInfo>): void {
    const t = findTurn(turnId);
    if (!t) return;
    // 先找 final，找不到找 process（含已搬运的中途卡）
    const all: ToolCallInfo[][] = [t.final.toolCalls ?? [], ...t.process.map((m) => m.toolCalls ?? [])];
    for (const arr of all) {
      const tc = arr.find((x) => x.id === toolCallId);
      if (tc) {
        Object.assign(tc, p);
        return;
      }
    }
  }

  function patchFinal(turnId: string, p: Partial<MessageItem>): void {
    const t = findTurn(turnId);
    if (!t) return;
    Object.assign(t.final, p);
  }

  async function loadSnapshot(): Promise<void> {
    loading.value = true;
    try {
      const snap = await transport.request<{
        seq?: number;
        turns?: Array<Record<string, unknown>>;
        messages?: Array<Record<string, unknown>>;
        agent?: Record<string, unknown>;
        session?: Record<string, unknown>;
        usage?: Record<string, unknown>;
      }>('state.snapshot', null);
      applySnapshot(snap ?? {});
      try {
        const sessionStore = useSessionStore();
        const s = snap?.session;
        if (s && typeof s === 'object') {
          sessionStore.applyInfo({
            sessionId: typeof s.id === 'string' ? (s.id as string) : undefined,
            cwd: s.cwd as string | undefined,
            historyPath: (s.historyPath ?? null) as string | null,
          });
        }
        const a = snap?.agent;
        if (a && typeof a === 'object') sessionStore.applySnapshotAgent(a);
        const u = snap?.usage as { inputTotal?: unknown; outputTotal?: unknown; input?: unknown; output?: unknown } | undefined;
        if (u && typeof u === 'object') {
          sessionStore.applyInfo({
            usage: {
              input: typeof u.inputTotal === 'number' ? u.inputTotal : typeof u.input === 'number' ? u.input : 0,
              output: typeof u.outputTotal === 'number' ? u.outputTotal : typeof u.output === 'number' ? u.output : 0,
            },
          });
        }
      } catch {
        /* 会话元信息同步失败不影响消息绘制 */
      }
    } finally {
      loading.value = false;
    }
  }

  function clearItems(): void {
    turns.value = [];
    drafts.value = {};
    toolTail.value = new Map();
  }

  function resetCursor(): void {
    lastSeq.value = 0;
  }

  function clear(): void {
    clearItems();
  }

  return {
    items,
    turns,
    drafts,
    processOpen,
    toolTail,
    lastSeq,
    loading,
    mismatch,
    isEmpty,
    lastAssistant,
    applySnapshot,
    findTurn,
    ensureTurn,
    appendDraft,
    clearDraft,
    clearTurnDrafts,
    moveRoundToProcess,
    finalizeTurn,
    appendOptimisticUser,
    appendInjected,
    ensureToolCall,
    patchToolCall,
    patchFinal,
    loadSnapshot,
    clearItems,
    resetCursor,
    clear,
    nextId,
    setLastSeq,
  };
});

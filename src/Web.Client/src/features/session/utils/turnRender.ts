// P02 会话渲染：单轮单容器三段式的前端适配器（纯函数，不引入后端 TurnView 类型）。
// 稳态沿用 UserTurn.steps 原序，不再切分双数据源；末段只是末 step 的只读引用。
import type { UnifiedChatMessage } from '@shared/bridge/hostBridge';
import type { TimelineEntry, TurnGroup } from './turnGroup';
import type { UserTurn, UserTurnStep } from './userTurn';

/** 单步渲染视图（UserTurnStep 的只读投影，字段与渲染消费对齐）。 */
export interface StepView {
  iteration: number;
  assistantId: string;
  content: string;
  reasoning: string | null;
  toolMessages: UnifiedChatMessage[];
  telemetry: TurnGroup['telemetry'];
}

/** 单轮渲染视图（模板只消费它，不再同时读 earlier/last 双截面）。 */
export interface TurnRenderView {
  key: string;
  userMsg: UnifiedChatMessage | null;
  steps: StepView[];
  isRunning: boolean;
}

/** 流式段最小形态（DisplaySeg / StreamSeg 均可，字段按存在性读取）。 */
export interface LiveSegLike {
  kind: string;
  key: string;
  text?: string;
  iteration?: number;
  callId?: string;
  card?: { callId?: string; functionName?: string } | null;
}

export function stepViewOf(step: UserTurnStep): StepView {
  return {
    iteration: step.iteration,
    assistantId: String(step.assistantId || ''),
    content: String(step.content || ''),
    reasoning: step.reasoning ? String(step.reasoning) : null,
    toolMessages: Array.isArray(step.toolMessages) ? step.toolMessages.slice() : [],
    telemetry: step.telemetry || null,
  };
}

export function buildTurnRenderView(turn: UserTurn): TurnRenderView {
  return {
    key: String(turn.key || ''),
    userMsg: turn.userMsg || null,
    steps: (turn.steps || []).map(stepViewOf),
    isRunning: turn.isRunning === true,
  };
}

/** 为 ToolCallCard 现场组装的 TurnGroup 薄壳（只读引用，不复制消息体）。 */
export function groupOf(step: StepView): TurnGroup {
  const toolCalls = (step.toolMessages || []).map((m) => {
    const raw = m as unknown as { toolCallId?: string; id?: string; name?: string };
    return { id: String(raw.toolCallId || raw.id || ''), name: String(raw.name || 'tool') };
  });
  return {
    key: String(step.assistantId || `step-${step.iteration}`),
    assistantId: String(step.assistantId || ''),
    reasoning: step.reasoning,
    content: step.content,
    toolCalls,
    toolMessages: step.toolMessages,
    telemetry: step.telemetry,
    createdAtUtc: '',
  };
}

function persistedToolIds(turn: Pick<UserTurn, 'steps'>): Set<string> {
  const out = new Set<string>();
  for (const st of turn.steps || []) {
    for (const m of st.toolMessages || []) {
      const raw = m as unknown as { toolCallId?: string; id?: string };
      const callId = String(raw.toolCallId || '');
      if (callId) out.add(callId);
      const id = String(raw.id || '');
      if (id) out.add(id);
    }
    for (const tc of st.toolCalls || []) {
      if (tc && tc.id) out.add(String(tc.id));
    }
  }
  return out;
}

function segCallId(seg: LiveSegLike): string {
  if (seg.callId) return String(seg.callId);
  const card = seg.card as { callId?: string } | null | undefined;
  if (card && card.callId) return String(card.callId);
  return '';
}

/**
 * 流式尾槽：已落盘归属的 tool 段不再重复渲染（毕业即被稳态 step 替换），
 * 无归属的尾 text / 新 tool 段挂尾槽，与稳态同 key 容器内追加。
 */
export function pendingTailSlot(liveSegs: LiveSegLike[] | null | undefined, turn: Pick<UserTurn, 'steps'>): LiveSegLike[] {
  if (!Array.isArray(liveSegs) || !liveSegs.length) return [];
  const persisted = persistedToolIds(turn);
  return liveSegs.filter((seg) => {
    if (!seg) return false;
    if (String(seg.kind || '') === 'tool') {
      const cid = segCallId(seg);
      if (cid && persisted.has(cid)) return false;
      return true;
    }
    return true;
  });
}

/** 轮内 step 锚点（目录跳转与阅读定位共用，格式恒定）。 */
export function turnStepAnchorKey(turnKey: string, iteration: number): string {
  return `${String(turnKey || '')}:step-${String(iteration)}`;
}

type EntryLike = { kind?: string; key?: string; turn?: { key?: string } };

/**
 * Live 唯一归属：全量条目中最后一个用户轮拥有流式段，其余轮返回空。
 * 与 TimelineView 内的轮过滤同语义，抽到此处供模板与单测共用。
 */
export function isLiveOwner(entry: EntryLike, items: EntryLike[]): boolean {
  if (!entry || entry.kind !== 'user-turn') return false;
  let owner = '';
  for (let i = items.length - 1; i >= 0; i--) {
    if (items[i] && items[i].kind === 'user-turn') {
      owner = String(items[i].turn?.key || items[i].key || '');
      break;
    }
  }
  const key = String(entry.turn?.key || entry.key || '');
  return !!key && key === owner;
}

/**
 * 运行态展平（旧 turn-run 入口的渲染侧复用）：把合并舱展开为逐 step 的 turn 条目，
 * 其余 kind 原样透传；只新建 entry 外壳，不改动 group/消息对象。
 */
export function flattenTurnRunsForRender(entries: TimelineEntry[]): TimelineEntry[] {
  if (!Array.isArray(entries)) return [];
  const out: TimelineEntry[] = [];
  for (const e of entries) {
    if (e && e.kind === 'turn-run' && e.run && Array.isArray(e.run.groups)) {
      for (const g of e.run.groups) {
        out.push({ key: String(g.key || ''), kind: 'turn', group: g });
      }
      continue;
    }
    out.push(e);
  }
  return out;
}

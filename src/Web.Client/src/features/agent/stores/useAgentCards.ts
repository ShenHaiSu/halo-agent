/** need21-F03：卡片 store（ToolCard/附件位/subAgents 聚合）。
 * 工具事件分支自旧 handleTurnEvent 原样搬入；workspace 侧效改走 runtime 上下文（旧转发注入实现）。
 * 跨 store 读：无（只读 model 单例 + haloEventBus 事件 + runtime 接口）。
 */
import { defineStore } from 'pinia';
import { computed } from 'vue';
import { haloEventBus } from '@shared/bridge/hostBridge';
import { estimateUtf8Bytes } from '@shared/utils/tokenEstimate';
import {
  activeSessionId,
  cardById,
  dirtyPersisted,
  ensureSessionSync,
  ensureTurn,
  markToolSegPersisted,
  pushToolSeg,
  rebuildIndex,
  resolveTargetSession,
  touch,
  truncateText,
  type AgentToolCard,
  type TurnEventPayload,
} from './model';
import { agentRuntime } from './runtime';
import { flushSegBufSync } from './useAgentStream';

let listenersBound = false;

function pickArgs(payload: Record<string, unknown>): string {
  const cands = [payload['argsJson'], payload['argumentsJson'], payload['arguments']];
  for (const c of cands) {
    if (typeof c === 'string' && c) return c;
  }
  return '';
}

/** need13：子进度聚合（主桶隔离；subId 事件不污染主时间线，只更新 items） */
function handleSubEvent(sid: string, kind: string, payload: Record<string, unknown> | null | undefined): void {
  const t = ensureTurn(sid);
  const p = payload ?? {};
  if (kind === 'SubAgentsStarted') {
    const subs = Array.isArray(p['subs']) ? (p['subs'] as Array<Record<string, unknown>>) : [];
    t.subAgents.total = typeof p['total'] === 'number' ? (p['total'] as number) : subs.length;
    t.subAgents.waiting = true;
    for (const s of subs) {
      const subId = String(s['subId'] ?? '');
      if (subId && !t.subAgents.items.some((i) => i.subId === subId)) {
        t.subAgents.items.push({ subId, label: String(s['label'] ?? ''), status: 'running' });
      }
    }
    touch(t);
    return;
  }
  if (kind === 'SubAgentProgress' || kind === 'agent:sub-event') {
    const subId = String(p['subId'] ?? '');
    const status = String(p['status'] ?? 'running');
    if (subId) {
      const ex = t.subAgents.items.find((i) => i.subId === subId);
      if (ex) ex.status = status;
      else t.subAgents.items.push({ subId, label: String(p['label'] ?? ''), status });
    }
    t.subAgents.done = t.subAgents.items.filter(
      (i) => i.status === 'succeeded' || i.status === 'failed' || i.status === 'cancelled'
    ).length;
    touch(t);
    return;
  }
  if (kind === 'SubAgentsBarrier') {
    t.subAgents.waiting = true;
    if (typeof p['total'] === 'number') t.subAgents.total = p['total'] as number;
    touch(t);
    return;
  }
  if (kind === 'SubAgentsBarrierReleased') {
    t.subAgents.waiting = false;
    const subs = Array.isArray(p['subs']) ? (p['subs'] as Array<Record<string, unknown>>) : [];
    for (const s of subs) {
      const subId = String(s['subId'] ?? s['callId'] ?? '');
      const ex = t.subAgents.items.find((i) => i.subId === subId);
      if (ex) ex.status = (s['success'] as boolean) ? 'succeeded' : 'failed';
    }
    t.subAgents.done = t.subAgents.total || t.subAgents.items.length;
    touch(t);
    return;
  }
}

function handleCardEvent(kind: string, payload: Record<string, unknown>, sid: string): void {
  const t = ensureTurn(sid);
  const num = (v: unknown): number => (typeof v === 'number' ? v : Number(v) || 0);
  const str = (v: unknown): string => (v === null || v === undefined ? '' : String(v));
  switch (kind) {
    case 'ToolCallStarted': {
      const callId = str(payload['callId']);
      const functionName = str(payload['functionName']) || 'tool';
      const args = pickArgs(payload);
      const rb = typeof payload['receivedBytes'] === 'number' ? (payload['receivedBytes'] as number) : 0;
      const pit = num(payload['iteration']);
      if (pit > t.curIteration) t.curIteration = pit;
      flushSegBufSync(sid);
      if (callId && cardById(t, callId) === null) {
        t.toolCards.push({
          callId,
          functionName,
          argsJson: args,
          status: 'running',
          resultText: '',
          exitCode: null,
          durationMs: null,
          iteration: num(payload['iteration']),
          receivedBytes: rb,
        });
        rebuildIndex(t);
        pushToolSeg(t, callId);
      } else if (callId && args) {
        const ex = cardById(t, callId);
        if (ex && !ex.argsJson) ex.functionName = functionName || ex.functionName;
        if (ex && !ex.argsJson) ex.argsJson = args;
        if (ex) ex.receivedBytes = rb;
        pushToolSeg(t, callId);
      } else if (callId) {
        pushToolSeg(t, callId);
      }
      touch(t);
      break;
    }
    case 'ToolCallUpdated':
    case 'ToolCallArgs':
    case 'ToolCallCompleted': {
      const callId = str(payload['callId']);
      const args = pickArgs(payload);
      const argsDelta = typeof payload['argsDelta'] === 'string' ? (payload['argsDelta'] as string) : '';
      const hasBytes = typeof payload['receivedBytes'] === 'number';
      const fallbackBytes = argsDelta ? estimateUtf8Bytes(argsDelta) : 0;
      const card = cardById(t, callId);
      if (card) {
        if (args) {
          card.argsJson = args;
          if (kind === 'ToolCallCompleted') card.receivedBytes = estimateUtf8Bytes(args);
          else if (hasBytes) card.receivedBytes = num(payload['receivedBytes']);
          else if (fallbackBytes > 0) card.receivedBytes = (card.receivedBytes || 0) + fallbackBytes;
        } else if (hasBytes) {
          card.receivedBytes = num(payload['receivedBytes']);
        } else if (fallbackBytes > 0) {
          card.receivedBytes = (card.receivedBytes || 0) + fallbackBytes;
        }
        if (payload['functionName']) card.functionName = str(payload['functionName']);
      } else if (callId && (args || argsDelta || hasBytes)) {
        t.toolCards.push({
          callId,
          functionName: str(payload['functionName']) || 'tool',
          argsJson: args,
          status: 'running',
          resultText: '',
          exitCode: null,
          durationMs: null,
          iteration: num(payload['iteration']),
          receivedBytes: hasBytes ? num(payload['receivedBytes']) : fallbackBytes,
        });
        rebuildIndex(t);
        pushToolSeg(t, callId);
      }
      touch(t);
      break;
    }
    case 'ToolExecutionStarted': {
      const callId = str(payload['callId']);
      const args = pickArgs(payload);
      const card = cardById(t, callId);
      if (card) {
        card.status = 'running';
        if (payload['functionName']) card.functionName = str(payload['functionName']);
        if (args) card.argsJson = args;
      } else if (callId) {
        t.toolCards.push({
          callId,
          functionName: str(payload['functionName']) || 'tool',
          argsJson: args,
          status: 'running',
          resultText: '',
          exitCode: null,
          durationMs: null,
          iteration: num(payload['iteration']),
          receivedBytes: 0,
        });
        rebuildIndex(t);
        pushToolSeg(t, callId);
      }
      touch(t);
      break;
    }
    case 'ToolMessagePersisted': {
      // need23-R1：tool 消息落盘即增量合并进视图（不再等下一轮全量重拉）
      const callId = str(payload['callId']);
      const messageId = str(payload['messageId']);
      const card = cardById(t, callId);
      if (card) {
        card.messageId = messageId || card.messageId;
        card.persisted = true;
      }
      // need23-R7：该工具的回包已落盘 → 让对应工具段毕业（从活动容器移交给稳态折叠栏）
      markToolSegPersisted(t, callId);
      try {
        agentRuntime().mergePersistedMessage(sid, {
          id: messageId,
          role: 'tool',
          content: str(payload['content']),
          toolCallId: str(payload['toolCallId']) || callId,
          name: str(payload['functionName']),
          createdAtUtc: str(payload['createdAtUtc']) || new Date().toISOString(),
          images: Array.isArray(payload['images']) ? payload['images'] : null,
        });
      } catch (e) {
        console.warn('[agent-cards] mergePersistedMessage failed:', e);
      }
      touch(t);
      break;
    }
    case 'ToolExecutionCompleted': {
      const callId = str(payload['callId']);
      const card = cardById(t, callId);
      if (card) {
        const success = payload['success'] !== false;
        card.status = success ? 'done' : 'error';
        card.exitCode = payload['exitCode'] === null || payload['exitCode'] === undefined ? null : num(payload['exitCode']);
        card.durationMs = payload['durationMs'] === null || payload['durationMs'] === undefined ? null : num(payload['durationMs']);
        const args = pickArgs(payload);
        if (args) card.argsJson = args;
        const rt = payload['resultText'];
        if (typeof rt === 'string' && rt) card.resultText = truncateText(rt);
        const fb = payload['fileBytes'];
        card.fileBytes = typeof fb === 'number' && Number.isFinite(fb) ? fb : null;
      }
      // need04-03：工具完成只内存更新+标脏，不全量拉取；等 AssistantMessagePersisted 再对账
      dirtyPersisted.value[sid] = true;
      touch(t);
      agentRuntime().touchSessionTime(sid, str(payload['updatedAtUtc']) || undefined);
      void agentRuntime()
        .refreshBudget()
        .catch(() => undefined);
      break;
    }
    default:
      break;
  }
  // need13：子代理聚合事件（agent:sub-event 由桥接层转发为 turn-event，kind 保持 SubAgentProgress）
  if (
    kind === 'SubAgentsStarted' ||
    kind === 'SubAgentProgress' ||
    kind === 'SubAgentsBarrier' ||
    kind === 'SubAgentsBarrierReleased' ||
    kind === 'agent:sub-event'
  ) {
    handleSubEvent(sid, kind, payload);
  }
}

function bindCardsListeners(): void {
  if (listenersBound) return;
  listenersBound = true;
  ensureSessionSync();
  haloEventBus.on<TurnEventPayload>('agent:turn-event', (evt) => {
    if (!evt || typeof evt.kind !== 'string') return;
    const sid = resolveTargetSession(evt.sessionId);
    if (!sid) return;
    handleCardEvent(evt.kind, evt.payload || {}, sid);
  });
}

export const useAgentCards = defineStore('agent-cards', () => {
  bindCardsListeners();

  const activeTurnRef = computed(() => ensureTurn(activeSessionId.value));

  const toolCards = computed<AgentToolCard[]>({
    get: () => activeTurnRef.value.toolCards,
    set: (v) => {
      activeTurnRef.value.toolCards = v;
    },
  });
  // need13：主会话子进度别名（模板直接用 activeTurn.subAgents 亦可，此处供 PromptComposer 便捷绑定）
  const subAgents = computed(() => activeTurnRef.value.subAgents);

  function bindCards(): void {
    bindCardsListeners();
  }

  return {
    toolCards,
    subAgents,
    cardById,
    bindCards,
  };
});

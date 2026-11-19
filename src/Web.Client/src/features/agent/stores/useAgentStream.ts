/** need21-F03：流式 store（分片合并/seg/进行态别名）。
 * seg 定时刷盘实现自 model 上移至此归属；cards/turn 经本模块 import 纯函数（非 store 直调）。
 * 跨 store 读：无（只读 model 单例 + haloEventBus 事件）。
 */
import { defineStore } from 'pinia';
import { computed, ref } from 'vue';
import { haloEventBus, type SessionStreamEventDto } from '@shared/bridge/hostBridge';
import { estTokens } from '@shared/utils/tokenEstimate';
import {
  activeSessionId,
  demotePending,
  emptyTurnState,
  ensureSessionSync,
  ensureTurn,
  fallbackProtocol,
  pushTextSeg,
  rebuildIndex,
  resolveTargetSession,
  touch,
  turnStartMarks,
  turns,
  type StreamSeg,
  type TurnEventPayload,
} from './model';

import { enqueueFrame, flushNow as flushBatcherNow, messageKey } from '@shared/utils/frameBatcher';

// ---- seg 分片缓冲（frameBatcher 接管合批；单一有序队列保到达序；SEG_FLUSH_MS 旧定时器已删除，由 frameBatcher rAF 合批） ----
const SEG_DISPLAY_CAP = 20000;

interface StreamDeltaItem {
  kind: 'text' | 'reasoning';
  chunk: string;
  blockId: string | null;
  seq: number | null;
  tag: string | null;
}

interface SegBuf {
  /** 按事件到达顺序入列的 delta；flush 时按此顺序逐一入 segs。 */
  items: StreamDeltaItem[];
}

const segBufs: Record<string, SegBuf> = {};
function segBufOf(sid: string): SegBuf {
  let b = segBufs[sid];
  if (!b) {
    b = { items: [] };
    segBufs[sid] = b;
  }
  return b;
}

export function flushSegBuf(sid: string): void {
  const t = turns.value[sid];
  const buf = segBufs[sid];
  if (!t || !buf || buf.items.length === 0) return;
  for (const it of buf.items) {
    if (it.kind === 'text') {
      t.streamingText += it.chunk;
      pushTextSeg(t, 'text', it.chunk, it.blockId, it.seq, it.tag ?? 'pending');
    } else {
      t.streamingReasoning += it.chunk;
      pushTextSeg(t, 'reasoning', it.chunk, it.blockId, it.seq, it.tag ?? 'process');
    }
    t.telemetry.liveEst += estTokens(it.chunk);
  }
  buf.items.length = 0;
  t.streamingActive = true;
  t.isGenerating = true;
  touch(t);
}

/** 按到达序入列；同会话所有 delta 共用一个合批键，帧末一次按序 flush。 */
export function queueSegDelta(
  sid: string,
  kind: 'text' | 'reasoning',
  chunk: string,
  blockId?: string | null,
  seq?: number | null,
  tag?: string | null
): void {
  if (!chunk) return;
  ensureTurn(sid);
  const buf = segBufOf(sid);
  buf.items.push({ kind, chunk, blockId: blockId ?? null, seq: seq ?? null, tag: tag ?? null });
  enqueueFrame(messageKey(sid, 'segs'), () => {
    try {
      flushSegBuf(sid);
    } catch (e) {
      console.warn('[agent-stream] flushSegBuf failed:', e);
    }
  });
}
/** terminal 事件（message_end/tool_end/非流）先 flushNow，不等下一帧。 */
export function flushSegBufSync(sid: string): void {
  flushBatcherNow();
  flushSegBuf(sid);
}
export function dropSegBuf(sid: string): void {
  delete segBufs[sid];
}

let listenersBound = false;

function bindStreamListeners(): void {
  if (listenersBound) return;
  listenersBound = true;
  ensureSessionSync();
  haloEventBus.on<SessionStreamEventDto>('llm:stream-event', (evt) => {
    if (!evt || typeof evt.kind !== 'string') return;
    const sid = resolveTargetSession(evt.sessionId);
    if (!sid) return;
    if (evt.kind === 'TextDelta' && evt.textDelta) {
      queueSegDelta(sid, 'text', evt.textDelta, evt.blockId ?? null, evt.seq ?? null, evt.tag ?? 'pending');
    } else if (evt.kind === 'ReasoningDelta' && evt.textDelta) {
      queueSegDelta(sid, 'reasoning', evt.textDelta, evt.blockId ?? null, evt.seq ?? null, evt.tag ?? 'process');
    }
  });
  haloEventBus.on<TurnEventPayload>('agent:turn-event', (evt) => {
    if (!evt || typeof evt.kind !== 'string') return;
    if (evt.kind !== 'IterationStarted') return;
    const sid = resolveTargetSession(evt.sessionId);
    if (!sid) return;
    const t = ensureTurn(sid);
    const p = evt.payload || {};
    const it = typeof p['iteration'] === 'number' ? (p['iteration'] as number) : Number(p['iteration']) || 0;
    flushSegBufSync(sid);
    try {
      demotePending(t);
    } catch {
      /* optimistic tag demote never blocks */
    }
    if (it > 0) t.curIteration = it;
    else if (t.curIteration <= 0) t.curIteration = 1;
    // need23-R7：上一 iteration 中已落盘的工具卡直接移除（内容已在稳态折叠栏里），
    // 避免 toolCards 跨轮无限累积（会污染"本轮 N 个工具"计数与去重集合）。
    if (t.toolCards.some((c) => c.persisted)) {
      t.toolCards = t.toolCards.filter((c) => !c.persisted);
      rebuildIndex(t);
    }
    t.streamingActive = true;
    t.isGenerating = true;
    touch(t);
  });
}

export const useAgentStream = defineStore('agent-stream', () => {
  bindStreamListeners();

  const activeTurnRef = computed(() => ensureTurn(activeSessionId.value));

  // ---- legacy 别名（模板/旧代码继续用，实际读写当前会话那桶） ----
  const streamingText = computed<string>({
    get: () => activeTurnRef.value.streamingText,
    set: (v) => {
      activeTurnRef.value.streamingText = v;
    },
  });
  const streamingReasoning = computed<string>({
    get: () => activeTurnRef.value.streamingReasoning,
    set: (v) => {
      activeTurnRef.value.streamingReasoning = v;
    },
  });
  const streamingActive = computed<boolean>({
    get: () => activeTurnRef.value.streamingActive,
    set: (v) => {
      activeTurnRef.value.streamingActive = v;
    },
  });
  const isGenerating = computed<boolean>({
    get: () => activeTurnRef.value.isGenerating,
    set: (v) => {
      activeTurnRef.value.isGenerating = v;
    },
  });

  const segKillSwitch = ref(false);

  /** need23-R5：流式 Markdown 逃生开关（localStorage 'halo-stream-md' = 'off' → 回落纯文本）。默认开启。 */
  const streamMdKillSwitch = ref(false);
  try {
    streamMdKillSwitch.value = String(window.localStorage.getItem('halo-stream-md') || '').toLowerCase() === 'off';
  } catch {
    streamMdKillSwitch.value = false;
  }

  const hasStreamingContent = computed(() => {
    const t = activeTurnRef.value;
    return t.streamingActive || t.streamingText.length > 0 || t.streamingReasoning.length > 0;
  });

  // need04-03：一轮一容器显隐：流式文本/思考/工具卡任一存在即显示
  const hasActiveTurn = computed(() => {
    const t = activeTurnRef.value;
    return t.streamingActive || t.streamingText.length > 0 || t.streamingReasoning.length > 0 || t.toolCards.length > 0;
  });

  const visibleSegs = computed<StreamSeg[]>(() => {
    const t = activeTurnRef.value;
    if (!t || !t.segs) return [];
    return t.segs.filter((s) => !s.persisted);
  });

  function segDisplayText(seg: StreamSeg | null | undefined): string {
    const s = String(seg?.text || '');
    return s.length > SEG_DISPLAY_CAP ? '…(前文已折叠)…' + s.slice(s.length - SEG_DISPLAY_CAP) : s;
  }

  function resetTurnState(sessionId?: string): void {
    const sid = (sessionId ?? activeSessionId.value).trim();
    if (!sid) return;
    dropSegBuf(sid);
    turns.value[sid] = emptyTurnState(sid, fallbackProtocol.value);
    turnStartMarks[sid] = Date.now();
  }

  function clearTurn(sessionId: string): void {
    const sid = (sessionId || '').trim();
    if (!sid) return;
    dropSegBuf(sid);
    delete turns.value[sid];
    delete turnStartMarks[sid];
  }

  function bindStream(): void {
    bindStreamListeners();
  }

  return {
    streamingText,
    streamingReasoning,
    streamingActive,
    isGenerating,
    segKillSwitch,
    streamMdKillSwitch,
    hasStreamingContent,
    hasActiveTurn,
    visibleSegs,
    segDisplayText,
    resetTurnState,
    clearTurn,
    bindStream,
  };
});

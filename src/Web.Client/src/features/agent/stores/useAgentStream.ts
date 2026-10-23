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
  emptyTurnState,
  ensureSessionSync,
  ensureTurn,
  fallbackProtocol,
  pushTextSeg,
  resolveTargetSession,
  touch,
  turnStartMarks,
  turns,
  type StreamSeg,
  type TurnEventPayload,
} from './model';

import { enqueueFrame, flushNow as flushBatcherNow, messageKey } from '@shared/utils/frameBatcher';

// ---- seg 分片缓冲（frameBatcher 接管合批；SEG_FLUSH_MS 旧定时器已删除，由 frameBatcher rAF 合批） ----
const SEG_DISPLAY_CAP = 20000;
interface SegBuf {
  text: string;
  reasoning: string;
}
const segBufs: Record<string, SegBuf> = {};
function segBufOf(sid: string): SegBuf {
  let b = segBufs[sid];
  if (!b) {
    b = { text: '', reasoning: '' };
    segBufs[sid] = b;
  }
  return b;
}
export function flushSegBuf(sid: string): void {
  const t = turns.value[sid];
  const buf = segBufs[sid];
  if (!t || !buf) return;
  const hasText = !!buf.text;
  const hasReason = !!buf.reasoning;
  if (!hasText && !hasReason) return;
  if (hasText) {
    t.streamingText += buf.text;
    pushTextSeg(t, 'text', buf.text);
    t.telemetry.liveEst += estTokens(buf.text);
    buf.text = '';
  }
  if (hasReason) {
    t.streamingReasoning += buf.reasoning;
    pushTextSeg(t, 'reasoning', buf.reasoning);
    t.telemetry.liveEst += estTokens(buf.reasoning);
    buf.reasoning = '';
  }
  t.streamingActive = true;
  t.isGenerating = true;
  touch(t);
}
/** 同 key 合并：流文本 append 非替换；经 frameBatcher rAF 合批进 Pinia。 */
export function queueSegDelta(sid: string, kind: 'text' | 'reasoning', chunk: string): void {
  if (!chunk) return;
  ensureTurn(sid);
  const buf = segBufOf(sid);
  if (kind === 'text') buf.text += chunk;
  else buf.reasoning += chunk;
  enqueueFrame(messageKey(sid, kind), () => {
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
      queueSegDelta(sid, 'text', evt.textDelta);
    } else if (evt.kind === 'ReasoningDelta' && evt.textDelta) {
      queueSegDelta(sid, 'reasoning', evt.textDelta);
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
    if (it > 0) t.curIteration = it;
    else if (t.curIteration <= 0) t.curIteration = 1;
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
    hasStreamingContent,
    hasActiveTurn,
    visibleSegs,
    segDisplayText,
    resetTurnState,
    clearTurn,
    bindStream,
  };
});

import { defineStore } from 'pinia';
import { computed, ref } from 'vue';
import type { ContractEventFrame } from '@/contract/agent';
import type { EventBatch, FrameTransport, InvokeResult } from '@/transport/types';
import { MAX_BATCH } from '@/transport/types';
import type { UnifiedChatMessage } from '@/shared/bridge/hostBridge';
import { mapHistoryPage, readPageTotal, type FrameMappedMessage } from './frameHistoryMap';

export type { FrameMappedMessage } from './frameHistoryMap';

export interface FrameSessionItem {
  sessionId: string;
  title: string;
}

export interface ConvergeOptions {
  requestId?: string;
  timeoutMs?: number;
}

const HISTORY_PAGE = 50;
const CONVERGE_TIMEOUT_MS = 30000;
const POLL_STEP_MS = 60;

function newRequestId(): string {
  try {
    const cryptoRef = (globalThis as { crypto?: { randomUUID?: () => string } }).crypto;
    if (cryptoRef && typeof cryptoRef.randomUUID === 'function') {
      return cryptoRef.randomUUID();
    }
  } catch {
    /* fall through to the local fallback */
  }
  return `req-${Date.now().toString(36)}-${Math.floor(Math.random() * 1e6).toString(36)}`;
}

function sleep(ms: number): Promise<void> {
  return new Promise((resolve) => {
    setTimeout(resolve, ms);
  });
}

/**
 * Visible-window session view over the frame boundary. It keeps only the
 * active id, the list and the loaded page of one session; everything here
 * is discardable and rebuilds from snapshot plus paged history. The backend
 * stays the only holder of session truth. The transport is injected so the
 * whole-chain switch decides the backing without touching this logic.
 */
export const useFrameSessionStore = defineStore('frameSession', () => {
  const transport = ref<FrameTransport | null>(null);
  const activeSessionId = ref('');
  const latestSequence = ref(0);
  const sessions = ref<FrameSessionItem[]>([]);
  const detailTitle = ref('');
  const history = ref<FrameMappedMessage[]>([]);
  const cursor = ref<string | null>(null);
  const hasMore = ref(false);
  const total = ref(0);
  const skippedRows = ref(0);
  const loading = ref(false);
  const lastError = ref('');
  const pendingRequestId = ref('');
  const pendingText = ref('');
  const pendingAt = ref(0);

  const isBound = computed(() => transport.value !== null);
  const messageCount = computed(() => history.value.length);

  function bind(frameTransport: FrameTransport): void {
    transport.value = frameTransport;
  }

  function reset(): void {
    activeSessionId.value = '';
    latestSequence.value = 0;
    sessions.value = [];
    detailTitle.value = '';
    history.value = [];
    cursor.value = null;
    hasMore.value = false;
    total.value = 0;
    skippedRows.value = 0;
    lastError.value = '';
    dropPending();
  }

  function noteError(result: InvokeResult, fallback: string): boolean {
    if (result.ok) {
      lastError.value = '';
      return true;
    }
    lastError.value = result.error?.msg || fallback;
    return false;
  }

  function readString(record: Record<string, unknown>, key: string): string {
    const value = record[key];
    return typeof value === 'string' ? value : '';
  }

  function readNumber(record: Record<string, unknown>, key: string): number {
    const value = record[key];
    return typeof value === 'number' && Number.isFinite(value) ? value : 0;
  }

  /** Show a local prompt echo immediately; confirmation arrives later. */
  function showPendingPrompt(text: string, requestId: string): void {
    pendingText.value = text;
    pendingRequestId.value = requestId;
    pendingAt.value = Date.now();
  }

  function dropPending(): void {
    pendingRequestId.value = '';
    pendingText.value = '';
    pendingAt.value = 0;
  }

  /**
   * Whether the pending echo already has a durable twin: the newest user
   * rows carry the same content, so the overlay would render twice.
   */
  function isPendingConfirmed(): boolean {
    const want = pendingText.value;
    if (want === '') return true;
    let checked = 0;
    for (let index = history.value.length - 1; index >= 0 && checked < 5; index--) {
      const entry = history.value[index];
      if (!entry || String(entry.role ?? '').toLowerCase() !== 'user') continue;
      checked += 1;
      if (String(entry.content ?? '') === want) return true;
    }
    return false;
  }

  const hasPending = computed(() => pendingRequestId.value !== '' && !isPendingConfirmed());

  /** Render list: durable page plus the pending echo while it is unconfirmed. */
  const visibleHistory = computed<FrameMappedMessage[]>(() => {
    if (pendingRequestId.value === '' || isPendingConfirmed()) return history.value;
    const overlay: FrameMappedMessage = {
      id: `pending-${pendingRequestId.value}`,
      role: 'user',
      content: pendingText.value,
      reasoningContent: null,
      toolCalls: null,
      toolCallId: null,
      name: null,
      inputTokens: null,
      outputTokens: null,
      cachedTokens: null,
      createdAtUtc: new Date(pendingAt.value || Date.now()).toISOString(),
      images: null,
      truncated: false,
      backendId: '',
      spilledFile: null,
    };
    return [...history.value, overlay];
  });

  /** Rebuild the visible window after reload, reconnect or a sequence gap. */
  async function rebuildVisibleWindow(): Promise<boolean> {
    if (!(await refreshFromSnapshot())) return false;
    if (!(await loadList())) return false;
    if (activeSessionId.value !== '') {
      return await openSession(activeSessionId.value);
    }
    return true;
  }

  /** Rebuild the visible window after reload or reconnect. */
  async function refreshFromSnapshot(): Promise<boolean> {
    const channel = transport.value;
    if (!channel) {
      lastError.value = 'Frame transport is not bound.';
      return false;
    }
    loading.value = true;
    try {
      const snapshot = await channel.invoke('state.snapshot', {});
      if (!noteError(snapshot, 'Snapshot failed.')) return false;
      const result = snapshot.result ?? {};
      activeSessionId.value = readString(result, 'activeSessionId');
      latestSequence.value = readNumber(result, 'latestSequence');
      return true;
    } finally {
      loading.value = false;
    }
  }

  async function loadList(): Promise<boolean> {
    const channel = transport.value;
    if (!channel) {
      lastError.value = 'Frame transport is not bound.';
      return false;
    }
    loading.value = true;
    try {
      const reply = await channel.invoke('session.list', {});
      if (!noteError(reply, 'Session list failed.')) return false;
      const raw = reply.result?.['sessions'];
      sessions.value = Array.isArray(raw)
        ? raw
            .filter((entry): entry is Record<string, unknown> => !!entry && typeof entry === 'object')
            .map((entry) => ({
              sessionId: readString(entry, 'sessionId'),
              title: readString(entry, 'title'),
            }))
            .filter((entry) => entry.sessionId !== '')
        : [];
      return true;
    } finally {
      loading.value = false;
    }
  }

  /** Open one session: detail plus the first history page. */
  async function openSession(sessionId: string): Promise<boolean> {
    const channel = transport.value;
    if (!channel) {
      lastError.value = 'Frame transport is not bound.';
      return false;
    }
    const id = (sessionId || '').trim();
    if (!id) {
      lastError.value = 'Session id is required.';
      return false;
    }
    loading.value = true;
    try {
      const detail = await channel.invoke('session.get', { sessionId: id });
      if (!detail.ok && detail.error?.code === 'NOT_FOUND') {
        // Unknown sessions read as an empty page instead of an error.
        activeSessionId.value = id;
        detailTitle.value = '';
        history.value = [];
        cursor.value = null;
        hasMore.value = false;
        total.value = 0;
        skippedRows.value = 0;
        dropPending();
        lastError.value = '';
        return true;
      }
      if (!noteError(detail, 'Session detail failed.')) return false;
      activeSessionId.value = id;
      detailTitle.value = readString(detail.result ?? {}, 'title');
      history.value = [];
      cursor.value = null;
      total.value = 0;
      skippedRows.value = 0;
      dropPending();
      return await loadPage(channel, id, '');
    } finally {
      loading.value = false;
    }
  }

  /** Append the next history page toward older messages. */
  async function loadMore(): Promise<boolean> {
    const channel = transport.value;
    if (!channel) {
      lastError.value = 'Frame transport is not bound.';
      return false;
    }
    if (!activeSessionId.value || !hasMore.value) return false;
    loading.value = true;
    try {
      return await loadPage(channel, activeSessionId.value, cursor.value ?? '');
    } finally {
      loading.value = false;
    }
  }

  /**
   * Incremental catch-up after a turn settles: pull only the page past the
   * current cursor instead of reopening the session.
   */
  async function refreshActive(): Promise<boolean> {
    const channel = transport.value;
    if (!channel) {
      lastError.value = 'Frame transport is not bound.';
      return false;
    }
    if (!activeSessionId.value) {
      lastError.value = 'No active session.';
      return false;
    }
    loading.value = true;
    try {
      return await loadPage(channel, activeSessionId.value, cursor.value ?? '');
    } finally {
      loading.value = false;
    }
  }

  async function loadPage(channel: FrameTransport, sessionId: string, pageCursor: string): Promise<boolean> {
    const reply = await channel.invoke('session.history', {
      sessionId,
      cursor: pageCursor,
      limit: HISTORY_PAGE,
    });
    if (!noteError(reply, 'Session history failed.')) return false;
    const result = reply.result ?? {};
    const page = mapHistoryPage(result);
    history.value = [...history.value, ...page.messages];
    skippedRows.value += page.skipped;
    total.value = readPageTotal(result, history.value.length);
    const next = result['nextCursor'];
    cursor.value = typeof next === 'string' && next !== '' ? next : null;
    hasMore.value = result['hasMore'] === true && cursor.value !== null;
    return true;
  }

  function watchTurnDone(frames: ContractEventFrame[], turnId: string): boolean {
    for (const frame of frames || []) {
      if (!frame || frame.t !== 'evt' || frame.k !== 'turn.done') continue;
      const payload = (frame.p ?? {}) as Record<string, unknown>;
      if (String(payload['turnId'] ?? '') === turnId) return true;
    }
    return false;
  }

  async function waitForTurnDone(
    channel: FrameTransport,
    fromSeq: number,
    turnId: string,
    timeoutMs: number
  ): Promise<{ done: boolean; nextSeq: number }> {
    const deadline = Date.now() + Math.max(500, timeoutMs);
    let seq = Math.max(0, Math.floor(fromSeq));
    for (;;) {
      const batch: EventBatch | null = await channel.pollBatch(seq, MAX_BATCH).catch((): null => null);
      if (batch) {
        if (watchTurnDone(batch.frames, turnId)) {
          const last = batch.frames[batch.frames.length - 1];
          const next = typeof last?.seq === 'number' ? last.seq : batch.nextSeq;
          return { done: true, nextSeq: Math.max(seq, next, batch.nextSeq) };
        }
        if (typeof batch.nextSeq === 'number' && batch.nextSeq > seq) seq = batch.nextSeq;
      }
      if (Date.now() >= deadline) return { done: false, nextSeq: seq };
      await sleep(POLL_STEP_MS);
    }
  }

  /**
   * Frame send branch: echo the prompt at once, issue agent.prompt with a
   * unique request key, wait for the matching turn.done event, then settle
   * by pulling the incremental page. Failures always clear the echo.
   */
  async function promptAndConverge(text: string, options: ConvergeOptions = {}): Promise<boolean> {
    const channel = transport.value;
    const sessionId = activeSessionId.value;
    const prompt = (text || '').trim();
    if (!channel || !sessionId || !prompt) {
      lastError.value = channel ? 'Session and prompt text are required.' : 'Frame transport is not bound.';
      return false;
    }
    const requestId = (options.requestId || '').trim() || newRequestId();
    showPendingPrompt(prompt, requestId);
    const reply = await channel.invoke('agent.prompt', {
      sessionId,
      text: prompt,
      requestId,
    });
    if (!noteError(reply, 'Prompt failed.')) {
      dropPending();
      return false;
    }
    const turnId = readString(reply.result ?? {}, 'turnId');
    if (!turnId) {
      dropPending();
      return await refreshActive();
    }
    const timed = await waitForTurnDone(channel, latestSequence.value, turnId, options.timeoutMs ?? CONVERGE_TIMEOUT_MS);
    latestSequence.value = Math.max(latestSequence.value, timed.nextSeq);
    dropPending();
    const refreshed = await refreshActive();
    if (!timed.done) {
      lastError.value = 'Turn confirmation timed out.';
      return false;
    }
    return refreshed;
  }

  return {
    rebuildVisibleWindow,
    activeSessionId,
    latestSequence,
    sessions,
    detailTitle,
    history,
    visibleHistory,
    hasPending,
    cursor,
    hasMore,
    total,
    skippedRows,
    loading,
    lastError,
    isBound,
    messageCount,
    bind,
    reset,
    refreshFromSnapshot,
    loadList,
    openSession,
    loadMore,
    refreshActive,
    showPendingPrompt,
    dropPending,
    isPendingConfirmed,
    promptAndConverge,
  };
});

export type { UnifiedChatMessage };

import { defineStore } from 'pinia';
import { computed, ref } from 'vue';
import type { FrameTransport, InvokeResult } from '@/transport/types';

export interface FrameSessionItem {
  sessionId: string;
  title: string;
}

export interface FrameHistoryItem {
  index: number;
  role: string;
  text: string;
  turnId?: string;
}

const HISTORY_PAGE = 50;

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
  const history = ref<FrameHistoryItem[]>([]);
  const cursor = ref<string | null>(null);
  const hasMore = ref(false);
  const loading = ref(false);
  const lastError = ref('');

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
    lastError.value = '';
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
      if (!noteError(detail, 'Session detail failed.')) return false;
      activeSessionId.value = id;
      detailTitle.value = readString(detail.result ?? {}, 'title');
      history.value = [];
      cursor.value = null;
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

  async function loadPage(channel: FrameTransport, sessionId: string, pageCursor: string): Promise<boolean> {
    const reply = await channel.invoke('session.history', {
      sessionId,
      cursor: pageCursor,
      limit: HISTORY_PAGE,
    });
    if (!noteError(reply, 'Session history failed.')) return false;
    const result = reply.result ?? {};
    const raw = result['items'] ?? result['messages'];
    const items = Array.isArray(raw)
      ? raw
          .filter((entry): entry is Record<string, unknown> => !!entry && typeof entry === 'object')
          .map((entry) => ({
            index: readNumber(entry, 'index'),
            role: readString(entry, 'role'),
            text: readString(entry, 'text'),
            turnId: readString(entry, 'turnId') || undefined,
          }))
      : [];
    history.value = [...history.value, ...items];
    const next = result['nextCursor'];
    cursor.value = typeof next === 'string' && next !== '' ? next : null;
    hasMore.value = result['hasMore'] === true && cursor.value !== null;
    return true;
  }

  return {
    rebuildVisibleWindow,
    activeSessionId,
    latestSequence,
    sessions,
    detailTitle,
    history,
    cursor,
    hasMore,
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
  };
});

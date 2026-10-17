import { defineStore } from 'pinia';
import { ref, computed } from 'vue';

export interface AskUserOption {
  label: string;
  description?: string | null;
}

export interface AskUserQuestion {
  id: string;
  question: string;
  options?: AskUserOption[] | null;
  multiSelect?: boolean | null;
  allowCustom?: boolean | null;
  default?: string | string[] | null;
}

export interface AskUserPending {
  sessionId: string;
  ticketId: string;
  callId: string;
  questions: AskUserQuestion[];
  arrivedAt: number;
}

export interface AskUserAnswerDraft {
  selected: string[];
  customInput: string;
}

function normalizeQuestions(raw: unknown): AskUserQuestion[] {
  if (!Array.isArray(raw)) return [];
  const out: AskUserQuestion[] = [];
  for (const q of raw as Array<Record<string, unknown>>) {
    if (!q || typeof q !== 'object') continue;
    const id = String((q['id'] ?? q['key'] ?? '') || '').trim();
    const question = String((q['question'] ?? q['text'] ?? q['title'] ?? '') || '').trim();
    if (!id || !question) continue;
    const rawOpts = q['options'];
    let options: AskUserOption[] | null = null;
    if (Array.isArray(rawOpts)) {
      options = (rawOpts as unknown[]).slice(0, 6).map((o) => {
        if (typeof o === 'string') return { label: o };
        const oo = o as Record<string, unknown>;
        return {
          label: String(oo['label'] ?? oo['value'] ?? oo['id'] ?? ''),
          description: oo['description'] != null ? String(oo['description']) : null,
        };
      }).filter((o) => o.label.trim().length > 0);
    }
    out.push({
      id,
      question,
      options,
      multiSelect: q['multiSelect'] === true,
      allowCustom: q['allowCustom'] !== false,
      default: (q['default'] ?? q['defaultValue'] ?? null) as AskUserQuestion['default'],
    });
  }
  return out.slice(0, 4);
}

function defaultDraftFor(q: AskUserQuestion): AskUserAnswerDraft {
  const d: AskUserAnswerDraft = { selected: [], customInput: '' };
  try {
    const def = q.default;
    if (typeof def === 'string' && def.trim()) {
      d.selected = [def];
    } else if (Array.isArray(def)) {
      d.selected = def.map((s) => String(s)).filter(Boolean);
    }
  } catch { /* ignore */ }
  return d;
}

/**
 * need09-06 问询态（独立分桶，禁止复用 agentStore.turns）。
 * pendingBySession: sessionId -> { ticketId, callId, questions, arrivedAt }
 * modal 草稿: draftBySession[sessionId][questionId] -> { selected, customInput }
 */
export const useAskUserStore = defineStore('askUser', () => {
  const pendingBySession = ref<Record<string, AskUserPending>>({});
  const modalSessionId = ref<string | null>(null);
  const modalVisible = ref(false);
  const draftBySession = ref<Record<string, Record<string, AskUserAnswerDraft>>>({});

  function getPending(sessionId: string): AskUserPending | null {
    const sid = (sessionId || '').trim();
    if (!sid) return null;
    return pendingBySession.value[sid] || null;
  }

  function hasPending(sessionId: string): boolean {
    return getPending(sessionId) !== null;
  }

  function pendingCount(sessionId: string): number {
    return hasPending(sessionId) ? 1 : 0;
  }

  const anyPendingCount = computed(() => Object.keys(pendingBySession.value).length);

  function ensureDraft(sessionId: string, questions: AskUserQuestion[]): Record<string, AskUserAnswerDraft> {
    const sid = (sessionId || '').trim();
    if (!sid) return {};
    let bucket = draftBySession.value[sid];
    if (!bucket) {
      bucket = {};
      draftBySession.value[sid] = bucket;
    }
    for (const q of questions) {
      if (!bucket[q.id]) bucket[q.id] = defaultDraftFor(q);
    }
    // 清理已不存在的题目草稿
    for (const key of Object.keys(bucket)) {
      if (!questions.some((q) => q.id === key) && questions.length > 0) delete bucket[key];
    }
    return bucket;
  }

  function getDraft(sessionId: string): Record<string, AskUserAnswerDraft> {
    const sid = (sessionId || '').trim();
    return (sid && draftBySession.value[sid]) || {};
  }

  function putPending(p: { sessionId: string; ticketId: string; callId: string; questions: unknown }): AskUserPending | null {
    const sid = String(p.sessionId || '').trim();
    if (!sid) return null;
    const questions = normalizeQuestions(p.questions);
    if (!questions.length) return null;
    const pending: AskUserPending = {
      sessionId: sid,
      ticketId: String(p.ticketId || ''),
      callId: String(p.callId || ''),
      questions,
      arrivedAt: Date.now(),
    };
    pendingBySession.value[sid] = pending;
    ensureDraft(sid, questions);
    return pending;
  }

  function openModal(sessionId: string): boolean {
    const sid = (sessionId || '').trim();
    if (!sid) return false;
    const pending = pendingBySession.value[sid];
    if (!pending) return false;
    ensureDraft(sid, pending.questions);
    modalSessionId.value = sid;
    modalVisible.value = true;
    return true;
  }

  function closeModal(): void {
    modalVisible.value = false;
    modalSessionId.value = null;
  }

  function clearPending(sessionId: string, ticketId?: string): void {
    const sid = (sessionId || '').trim();
    if (!sid) return;
    const cur = pendingBySession.value[sid];
    if (cur && ticketId && cur.ticketId && String(ticketId) !== String(cur.ticketId)) {
      return;
    }
    delete pendingBySession.value[sid];
    delete draftBySession.value[sid];
    if (modalSessionId.value === sid) {
      modalVisible.value = false;
      modalSessionId.value = null;
    }
  }

  return {
    pendingBySession,
    modalSessionId,
    modalVisible,
    draftBySession,
    anyPendingCount,
    getPending,
    hasPending,
    pendingCount,
    ensureDraft,
    getDraft,
    putPending,
    openModal,
    closeModal,
    clearPending,
  };
});

import { beforeEach, describe, expect, it } from 'vitest';
import { createPinia, setActivePinia } from 'pinia';
import type { EventBatch, FrameTransport, InvokeResult } from '@/transport/types';
import { mapHistoryPage } from './frameHistoryMap';
import { useFrameSessionStore } from './frameSessionStore';
import {
  buildFrameEntries,
  resolveHistoryMode,
  selectHistoryMessages,
  summarizeFrameMessages,
  useFrameHistoryView,
} from './frameHistoryView';
import { HISTORY_LEGACY_BADGE } from './historySourceGuard';
import { resolveMode } from './surfaceRouter';

/** Minimal frame backing: one session with a tool-carrying turn. */
function viewStub(): FrameTransport {
  const rows = [
    { index: 1, role: 'user', text: 'look up the file', turnId: 'turn-1' },
    {
      index: 2,
      role: 'assistant',
      text: 'on it',
      turnId: 'turn-1',
      reasoningContent: 'search first',
      toolCalls: [{ id: 'call-1', name: 'read_file', arguments: '{}' }],
      inputTokens: 10,
      outputTokens: 4,
      cachedTokens: 2,
    },
    { index: 3, role: 'tool', text: 'file body', turnId: 'turn-1', toolCallId: 'call-1' },
  ];
  return {
    invoke: async (method: string, params: Record<string, unknown> = {}): Promise<InvokeResult> => {
      if (method === 'session.get') {
        return { ok: true, result: { sessionId: params['sessionId'], title: 'Demo' } };
      }
      if (method === 'session.history') {
        return {
          ok: true,
          result: { sessionId: params['sessionId'], items: rows, nextCursor: '3', hasMore: false, total: 3 },
        };
      }
      return { ok: false, error: { code: 'NOT_FOUND', msg: `unexpected ${method}` } };
    },
    pollBatch: async (sinceSeq: number): Promise<EventBatch> => ({
      frames: [],
      nextSeq: sinceSeq,
      hasGap: false,
      droppedEstimate: 0,
    }),
  };
}

describe('frame history view source', () => {
  beforeEach(() => {
    setActivePinia(createPinia());
  });

  it('resolves the history face to frame under the development override', () => {
    expect(resolveHistoryMode(['history'])).toBe('frame');
    expect(resolveHistoryMode([])).toBe('legacy');
    expect(resolveHistoryMode(['turn'])).toBe('legacy');
  });

  it('lets the owned faces ride frame while unowned faces stay legacy', () => {
    const override = ['history', 'turn', 'tool', 'usage', 'settings'] as const;
    expect(resolveMode('history', [...override])).toBe('frame');
    expect(resolveMode('turn', [...override])).toBe('frame');
    expect(resolveMode('tool', [...override])).toBe('frame');
    expect(resolveMode('usage', [...override])).toBe('legacy');
    expect(resolveMode('settings', [...override])).toBe('legacy');
  });

  it('picks the backing list by resolved mode', () => {
    const legacy = ['a'];
    const frame = ['b'];
    expect(selectHistoryMessages('frame', legacy, frame)).toEqual(['b']);
    expect(selectHistoryMessages('legacy', legacy, frame)).toEqual(['a']);
  });

  it('groups mapped rows through the shared timeline pipeline', () => {
    const page = mapHistoryPage({
      items: [
        { index: 1, role: 'user', text: 'look up the file', turnId: 'turn-1' },
        {
          index: 2,
          role: 'assistant',
          text: 'on it',
          turnId: 'turn-1',
          reasoningContent: 'search first',
          toolCalls: [{ id: 'call-1', name: 'read_file', arguments: '{}' }],
        },
        { index: 3, role: 'tool', text: 'file body', turnId: 'turn-1', toolCallId: 'call-1' },
      ],
      total: 3,
    });
    const entries = buildFrameEntries(page.messages);
    const kinds = entries.map((entry) => entry.kind);
    expect(kinds).toContain('user');
    expect(kinds).toContain('turn');
    const turn = entries.find((entry) => entry.kind === 'turn');
    expect(turn?.group?.toolMessages.length).toBe(1);
    expect(turn?.group?.reasoning).toBe('search first');
    expect(summarizeFrameMessages(page.messages, 3)).toEqual({
      rows: 3,
      cards: 1,
      missingImages: 0,
      total: 3,
    });
  });

  it('stays on the legacy chain by default', () => {
    const view = useFrameHistoryView([]);
    expect(view.mode.value).toBe('legacy');
    expect(view.active.value).toBe(false);
    expect(view.messages.value).toEqual([]);
    expect(view.entries.value).toEqual([]);
    expect(view.writeBadge.value).toBe('');
  });

  it('serves frame entries with the legacy badge under the override', async () => {
    const frame = useFrameSessionStore();
    frame.bind(viewStub());
    await frame.openSession('session-a');
    const view = useFrameHistoryView(['history']);
    expect(view.mode.value).toBe('frame');
    expect(view.active.value).toBe(true);
    expect(view.messages.value.length).toBe(3);
    expect(view.total.value).toBe(3);
    expect(view.entries.value.length).toBeGreaterThan(0);
    expect(view.summary.value).toEqual({ rows: 3, cards: 1, missingImages: 0, total: 3 });
    expect(view.writeBadge.value).toBe(HISTORY_LEGACY_BADGE);
  });
});

import { describe, expect, it } from 'vitest';
import { countMissingImages, countToolCards, mapHistoryPage, mapHistoryRow, readPageTotal, resolveFullTextTarget } from './frameHistoryMap';

function backendRow(): Record<string, unknown> {
  return {
    id: 'backend-msg-9',
    index: 3,
    role: 'assistant',
    text: 'planned work',
    truncated: false,
    turnId: 'turn-7',
    toolCallId: null,
    name: null,
    toolCalls: [{ id: 'call-1', name: 'read_file', arguments: '{"path":"a"}' }],
    images: null,
    inputTokens: 12,
    outputTokens: 34,
    cachedTokens: 5,
    reasoningContent: 'why this plan',
    createdAtUtc: '2026-11-01T00:00:00Z',
  };
}

describe('history row mapping', () => {
  it('maps a full backend row into the render shape', () => {
    const mapped = mapHistoryRow(backendRow());
    expect(mapped).not.toBeNull();
    expect(mapped?.id).toBe('msg_3_turn-7');
    expect(mapped?.role).toBe('assistant');
    expect(mapped?.content).toBe('planned work');
    expect(mapped?.reasoningContent).toBe('why this plan');
    expect(mapped?.inputTokens).toBe(12);
    expect(mapped?.outputTokens).toBe(34);
    expect(mapped?.cachedTokens).toBe(5);
    expect(mapped?.toolCalls).toEqual([{ id: 'call-1', type: 'function', function: { name: 'read_file', arguments: '{"path":"a"}' } }]);
    expect(mapped?.truncated).toBe(false);
    expect(mapped?.backendId).toBe('backend-msg-9');
    expect(mapped?.createdAtUtc).toBe('2026-11-01T00:00:00Z');
  });

  it('keeps the render key stable for the same index and turn', () => {
    const first = mapHistoryRow({ index: 8, role: 'user', text: 'hi', turnId: 'turn-2' });
    const second = mapHistoryRow({ index: 8, role: 'user', text: 'hi', turnId: 'turn-2' });
    expect(first?.id).toBe('msg_8_turn-2');
    expect(second?.id).toBe(first?.id);
  });

  it('reads the legacy capitalized backend shape', () => {
    const mapped = mapHistoryRow({
      Id: 'backend-msg-4',
      Index: 4,
      Role: 'assistant',
      Text: 'done',
      Truncated: false,
      TurnId: 'turn-4',
      ToolCalls: [{ Id: 'call-9', Name: 'grep_search', Arguments: '{}' }],
      Images: [
        {
          ImageId: 'img-1',
          FileName: 'shot.png',
          Mime: 'image/png',
          RelPath: 'attachments/shot.png',
          Width: 64,
          Height: 64,
          SizeBytes: 10,
          IsMissing: true,
        },
      ],
      InputTokens: 3,
      OutputTokens: 7,
      CachedTokens: 1,
      ReasoningContent: 'trace',
      CreatedAtUtc: '2026-11-02T00:00:00Z',
    });
    expect(mapped?.id).toBe('msg_4_turn-4');
    expect(mapped?.toolCalls?.[0]).toMatchObject({ id: 'call-9' });
    expect(mapped?.images?.[0]).toMatchObject({ imageId: 'img-1', lost: true });
    expect(mapped?.backendId).toBe('backend-msg-4');
  });

  it('reads pages under either envelope key and skips unreadable rows', () => {
    const page = mapHistoryPage({
      messages: [
        { index: 1, role: 'user', text: 'one' },
        null,
        42,
        { role: 'assistant', text: 'no index' },
        { index: 2, role: 'mystery', text: 'bad role' },
        { index: 2, role: 'assistant', text: 'two' },
      ],
    });
    expect(page.messages.map((entry) => entry.content)).toEqual(['one', 'two']);
    expect(page.skipped).toBe(4);
    const twin = mapHistoryPage({
      items: [{ index: 1, role: 'user', text: 'one' }],
    });
    expect(twin.messages.length).toBe(1);
    expect(twin.skipped).toBe(0);
  });

  it('treats unknown sessions and empty payloads as empty pages', () => {
    expect(mapHistoryPage(null)).toEqual({ messages: [], skipped: 0 });
    expect(mapHistoryPage({})).toEqual({ messages: [], skipped: 0 });
    expect(mapHistoryPage({ items: [], total: 0 })).toEqual({ messages: [], skipped: 0 });
    expect(readPageTotal({ items: [], total: 7 }, 0)).toBe(7);
    expect(readPageTotal({ messages: [] }, 4)).toBe(4);
  });

  it('folds tool result rows into shared cards by call id', () => {
    const page = mapHistoryPage([
      { index: 1, role: 'user', text: 'go' },
      {
        index: 2,
        role: 'assistant',
        text: 'working',
        turnId: 'turn-1',
        toolCalls: [
          { id: 'call-a', name: 'read_file', arguments: '{}' },
          { id: 'call-b', name: 'grep_search', arguments: '{}' },
        ],
      },
      { index: 3, role: 'tool', text: 'file body', turnId: 'turn-1', toolCallId: 'call-a' },
      { index: 4, role: 'tool', text: 'hits', turnId: 'turn-1', toolCallId: 'call-b' },
      { index: 5, role: 'tool', text: 'orphan output', turnId: 'turn-1', toolCallId: 'call-c' },
    ]);
    expect(countToolCards(page.messages)).toBe(3);
  });

  it('counts missing images for the placeholder path', () => {
    const page = mapHistoryPage([
      {
        index: 1,
        role: 'user',
        text: 'see [image:img-1]',
        images: [
          { imageId: 'img-1', relPath: 'attachments/a.png', lost: false },
          { imageId: 'img-2', relPath: 'attachments/gone.png', lost: true },
        ],
      },
    ]);
    expect(countMissingImages(page.messages)).toBe(1);
  });

  it('routes full-text reads without silent failure', () => {
    const plain = mapHistoryRow({ index: 1, role: 'user', text: 'short' });
    const cut = mapHistoryRow({ index: 2, role: 'assistant', text: 'cut', truncated: true });
    const spilled = mapHistoryRow({
      index: 3,
      role: 'assistant',
      text: 'cut',
      truncated: true,
      spilledFile: 'spill/turn-3.log',
    });
    expect(plain && resolveFullTextTarget(plain)).toEqual({ kind: 'inline', path: null });
    expect(cut && resolveFullTextTarget(cut)).toEqual({ kind: 'preview-only', path: null });
    expect(spilled && resolveFullTextTarget(spilled)).toEqual({
      kind: 'open-path',
      path: 'spill/turn-3.log',
    });
  });
});

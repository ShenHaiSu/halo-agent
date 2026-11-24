/**
 * 乐观 user 纯函数单测（摘要源对账）。
 * 覆盖：key 稳定 / 摘要精确命中 / 超时 / 多 pending / 切会话隔离。
 */
import { describe, expect, it } from 'vitest';
import {
  isPendingExpired,
  isPendingForSession,
  makePendingKey,
  makePendingUser,
  matchConfirmedBySummary,
  pendingPreviewOf,
} from '@features/session/utils/pendingUser';

describe('makePendingKey key 稳定', () => {
  it('同一 requestId 恒得同一 key，DOM 不重建锚点', () => {
    expect(makePendingKey('abc123')).toBe('pending-abc123');
    expect(makePendingKey('abc123')).toBe(makePendingKey('abc123'));
  });

  it('摘要源精确命中即转正', () => {
    const p = makePendingUser({ requestId: 'r1', sessionId: 's1', content: 'hi' });
    expect(p.key).toBe('pending-r1');
    expect(matchConfirmedBySummary(p, [{ lastRequestId: 'r1' }])).not.toBeNull();
  });

  it('摘要未命中不转正', () => {
    const p = makePendingUser({ requestId: 'r1', sessionId: 's1', content: 'hi' });
    expect(matchConfirmedBySummary(p, [{ lastRequestId: 'other' }])).toBeNull();
    expect(matchConfirmedBySummary(p, [])).toBeNull();
  });
});

describe('图片占位', () => {
  it('content 含 [image:attId] 原样存，images 预览直出', () => {
    const p = makePendingUser({
      requestId: 'r1',
      sessionId: 's1',
      content: '看这张图[image:att_1]',
      images: [{ imageId: 'att_1', fileName: 'a.png', mime: 'image/png', relPath: '', width: 0, height: 0, sizeBytes: 10, sha16: 'x' }],
      previews: { att_1: 'data:image/png;base64,AAA' },
    });
    expect(p.content).toContain('[image:att_1]');
    expect(pendingPreviewOf(p, 'att_1')).toBe('data:image/png;base64,AAA');
  });
});

describe('超时兜底', () => {
  it('30s 未确认标 expired（仍显示，下次摘要拉再试）', () => {
    const p = makePendingUser({ requestId: 'r1', sessionId: 's1', content: 'hi', createdAtUtc: '2026-10-26T10:00:00.000Z' });
    expect(isPendingExpired(p, Date.parse('2026-10-26T10:00:10.000Z'))).toBe(false);
    expect(isPendingExpired(p, Date.parse('2026-10-26T10:00:31.000Z'))).toBe(true);
  });

  it('failed/confirmed 不再算超时', () => {
    const p = makePendingUser({ requestId: 'r1', sessionId: 's1', content: 'hi', createdAtUtc: '2026-10-26T10:00:00.000Z' });
    p.status = 'failed';
    expect(isPendingExpired(p, Date.parse('2026-10-26T10:05:00.000Z'))).toBe(false);
  });
});

describe('多 pending / 切会话隔离', () => {
  it('同一会话先后两发 key 不同、互不干扰', () => {
    const a = makePendingUser({ requestId: 'ra', sessionId: 's1', content: 'first' });
    const b = makePendingUser({ requestId: 'rb', sessionId: 's1', content: 'second' });
    expect(a.key).not.toBe(b.key);
    a.status = 'superseded';
    expect(matchConfirmedBySummary(a, [{ lastRequestId: 'rb' }])).toBeNull();
    expect(a.status).toBe('superseded');
    expect(b.status).toBe('sending');
  });

  it('pending 只 overlay 源 session（切走即藏，回来按对账呈现）', () => {
    const p = makePendingUser({ requestId: 'r1', sessionId: 's1', content: 'hi' });
    expect(isPendingForSession(p, 's1')).toBe(true);
    expect(isPendingForSession(p, 's2')).toBe(false);
    expect(isPendingForSession(null, 's1')).toBe(false);
  });
});

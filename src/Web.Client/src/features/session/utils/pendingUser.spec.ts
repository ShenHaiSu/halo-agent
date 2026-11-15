/**
 * need02-P1：乐观 user 纯函数单测（docs/need-02/04-P1 §改动清单）。
 * 覆盖：key 稳定 / 内容指纹 / 图片占位 / 超时 / 多 pending / 切会话隔离。
 */
import { describe, expect, it } from 'vitest';
import type { UnifiedChatMessage } from '@shared/bridge/hostBridge';
import {
  isPendingExpired,
  isPendingForSession,
  makePendingKey,
  makePendingUser,
  matchConfirmed,
  pendingPreviewOf,
  toPendingMessage,
} from '@features/session/utils/pendingUser';

function userMsg(id: string, content: string, createdAtUtc: string, extra?: Partial<UnifiedChatMessage>): UnifiedChatMessage {
  return {
    id,
    role: 'user',
    content,
    reasoningContent: null,
    toolCalls: null,
    toolCallId: null,
    name: null,
    inputTokens: null,
    outputTokens: null,
    cachedTokens: null,
    createdAtUtc,
    images: null,
    ...extra,
  } as UnifiedChatMessage;
}

describe('makePendingKey key 稳定', () => {
  it('同一 requestId 恒得同一 key（D2=A，DOM 不重建锚点）', () => {
    expect(makePendingKey('abc123')).toBe('pending-abc123');
    expect(makePendingKey('abc123')).toBe(makePendingKey('abc123'));
  });

  it('toPendingMessage id 即 pending.key（timeline `u-<key>` 全程稳定）', () => {
    const p = makePendingUser({ requestId: 'r1', sessionId: 's1', content: 'hi' });
    expect(p.key).toBe('pending-r1');
    expect(toPendingMessage(p).id).toBe('pending-r1');
  });
});

describe('matchConfirmed 内容指纹', () => {
  it('尾部 5 条内按内容+时间窗口命中', () => {
    const now = '2026-10-26T10:00:00.000Z';
    const p = makePendingUser({ requestId: 'r1', sessionId: 's1', content: 'hello', createdAtUtc: now });
    const msgs = [
      userMsg('m1', 'other', '2026-10-26T09:59:00.000Z'),
      userMsg('m2', 'hello', '2026-10-26T10:00:10.000Z'),
    ];
    expect(matchConfirmed(p, msgs)?.id).toBe('m2');
  });

  it('内容不同不命中；时间窗外（>60s）不命中', () => {
    const p = makePendingUser({ requestId: 'r1', sessionId: 's1', content: 'hello', createdAtUtc: '2026-10-26T10:00:00.000Z' });
    expect(matchConfirmed(p, [userMsg('m1', 'HELLO', '2026-10-26T10:00:05.000Z')])).toBeNull();
    expect(matchConfirmed(p, [userMsg('m1', 'hello', '2026-10-26T10:05:00.000Z')])).toBeNull();
    expect(matchConfirmed(p, [userMsg('m1', 'hello', '2026-10-26T09:58:00.000Z')])).toBeNull();
  });

  it('精确式：后端回带 requestId 时直接命中（无视内容/时间）', () => {
    const p = makePendingUser({ requestId: 'req-9', sessionId: 's1', content: 'aaa' });
    const m = userMsg('m9', 'different', '2026-10-20T00:00:00.000Z', {
      requestId: 'req-9',
    } as unknown as Partial<UnifiedChatMessage>);
    expect(matchConfirmed(p, [m])?.id).toBe('m9');
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
    expect(toPendingMessage(p).content).toContain('[image:att_1]');
    expect(pendingPreviewOf(p, 'att_1')).toBe('data:image/png;base64,AAA');
  });

  it('有图 pending 不误命中无图同文消息', () => {
    const p = makePendingUser({
      requestId: 'r1',
      sessionId: 's1',
      content: 'hi[image:att_1]',
      createdAtUtc: '2026-10-26T10:00:00.000Z',
      images: [{ imageId: 'att_1', fileName: 'a.png', mime: 'image/png', relPath: '', width: 0, height: 0, sizeBytes: 1, sha16: 'x' }],
    });
    expect(matchConfirmed(p, [userMsg('m1', 'hi[image:att_1]', '2026-10-26T10:00:05.000Z')])).toBeNull();
  });
});

describe('超时兜底', () => {
  it('30s 未确认标 expired（仍显示，下次全量拉再试）', () => {
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
  it('同一会话先后两发 key 不同、互不干扰（重发新 requestId，旧 superseded）', () => {
    const a = makePendingUser({ requestId: 'ra', sessionId: 's1', content: 'first' });
    const b = makePendingUser({ requestId: 'rb', sessionId: 's1', content: 'second' });
    expect(a.key).not.toBe(b.key);
    a.status = 'superseded';
    expect(matchConfirmed(a, [userMsg('m1', 'second', new Date().toISOString())])).toBeNull();
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

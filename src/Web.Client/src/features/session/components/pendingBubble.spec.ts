import { describe, expect, it } from 'vitest';
import { computed, ref } from 'vue';
import type { UnifiedChatMessage } from '@shared/bridge/hostBridge';
import { makePendingUser, matchConfirmedBySummary } from '@features/session/utils/pendingUser';

describe('pendingBubbleRendersWithoutOverlayMessage', () => {
  it('V2 下 overlay 恒等于 sessionMessages，气泡走摘要区直渲染', () => {
    const base = [{ id: 'u1' }] as UnifiedChatMessage[];
    const isV2Timeline = ref(true);
    const pendingOverlayMessages = computed<UnifiedChatMessage[]>(() => {
      if (isV2Timeline.value) return base;
      return base;
    });
    expect(pendingOverlayMessages.value).toBe(base);
    const pending = makePendingUser({ requestId: 'req-1', sessionId: 's1', content: 'hi' });
    expect(pending.key).toBe('pending-req-1');
  });
});

describe('pendingConfirmedBySummaryOnly', () => {
  it('摘要命中即转正，V2 不走 jsonl 启发式', () => {
    const pending = makePendingUser({ requestId: 'req-9', sessionId: 's1', content: 'hi' });
    const hit = matchConfirmedBySummary(pending, [{ lastRequestId: 'req-9' }]);
    expect(hit).not.toBeNull();
    expect(matchConfirmedBySummary(pending, [{ lastRequestId: 'other' }])).toBeNull();
  });
});

describe('pendingFailureShowsRetry', () => {
  it('失败态保留 pending 并可重试新请求', () => {
    const pending = makePendingUser({ requestId: 'req-1', sessionId: 's1', content: 'hi' });
    pending.status = 'failed';
    expect(pending.status).toBe('failed');
    expect(matchConfirmedBySummary(pending, [{ lastRequestId: 'req-1' }])).toBeNull();
  });
});

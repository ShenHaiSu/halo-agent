// 摘要区顺序：历史稳态轮在前，待发送用户气泡紧随其后（流态视觉顺序与稳态同构）。
import { describe, expect, it } from 'vitest';
import { mount } from '@vue/test-utils';
import { nextTick } from 'vue';
import TimelineSummarySection from './TimelineSummarySection.vue';
import type { TurnSummary } from '@shared/bridge/hostBridge';
import { makePendingUser } from '../utils/pendingUser';

function makeSummary(over: Partial<TurnSummary> = {}): TurnSummary {
  return {
    turnId: 'uturn-u1',
    turnSeq: 1,
    iterEnd: 1,
    userPreview: 'first question',
    finalBlockId: 'b1',
    finalPreview: 'first answer',
    blocks: 1,
    tools: 0,
    failed: 0,
    durationMs: 800,
    chars: 12,
    status: 'completed',
    lastRequestId: 'req-1',
    createdAtUtc: new Date().toISOString(),
    updatedAtUtc: new Date().toISOString(),
    anchorKey: 'uturn-u1',
    ...over,
  } as TurnSummary;
}

describe('摘要区待发送气泡位置', () => {
  it('历史稳态轮排在待发送气泡之前', async () => {
    const { createPinia } = await import('pinia');
    const pending = makePendingUser({
      requestId: 'req-2',
      sessionId: 'session-1',
      content: 'second question',
    });
    const wrapper = mount(TimelineSummarySection, {
      props: { summaries: [makeSummary()], pending },
      global: { plugins: [createPinia()] },
    });
    await nextTick();
    const section = wrapper.find('[data-testid="timeline-summary-section"]');
    expect(section.exists()).toBe(true);
    const html = section.html();
    const turnPos = html.indexOf('timeline-turn-uturn-u1');
    const pendingPos = html.indexOf('pending-bubble');
    expect(turnPos).toBeGreaterThanOrEqual(0);
    expect(pendingPos).toBeGreaterThanOrEqual(0);
    expect(turnPos).toBeLessThan(pendingPos);
  });

  it('无待发送气泡时只渲染历史稳态轮', async () => {
    const { createPinia } = await import('pinia');
    const wrapper = mount(TimelineSummarySection, {
      props: { summaries: [makeSummary()] },
      global: { plugins: [createPinia()] },
    });
    await nextTick();
    expect(wrapper.find('[data-testid="timeline-turn-uturn-u1"]').exists()).toBe(true);
    expect(wrapper.find('[data-testid="pending-bubble"]').exists()).toBe(false);
  });
});

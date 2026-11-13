import { describe, expect, it } from 'vitest';
import { mount } from '@vue/test-utils';
import { nextTick } from 'vue';
import TimelineSummarySection from './TimelineSummarySection.vue';
import type { TurnSummary } from '@shared/bridge/hostBridge';

function makeSummary(over: Partial<TurnSummary> = {}): TurnSummary {
  return {
    turnId: 'uturn-u1',
    turnSeq: 1,
    iterEnd: 1,
    userPreview: '帮我看看这个报错',
    finalBlockId: 'b3',
    finalPreview: '已经定位到空指针',
    blocks: 3,
    tools: 1,
    failed: 0,
    durationMs: 1200,
    chars: 42,
    status: 'completed',
    lastRequestId: 'req',
    createdAtUtc: new Date().toISOString(),
    updatedAtUtc: new Date().toISOString(),
    anchorKey: 'uturn-u1',
    ...over,
  } as TurnSummary;
}

describe('TimelineSummarySection', () => {
  it('renders one flattened turn per summary with user, process and final anchors', async () => {
    const { createPinia } = await import('pinia');
    const wrapper = mount(TimelineSummarySection, {
      props: {
        summaries: [
          makeSummary({ turnId: 'uturn-u1', turnSeq: 1 }),
          makeSummary({ turnId: 'uturn-u2', turnSeq: 2, finalBlockId: null, finalPreview: '', tools: 2 }),
        ],
      },
      global: { plugins: [createPinia()] },
    });
    await nextTick();
    expect(wrapper.find('[data-testid="timeline-summary-section"]').exists()).toBe(true);
    expect(wrapper.findAll('[data-testid="timeline-turn-uturn-u1"]').length).toBe(1);
    expect(wrapper.findAll('[data-testid="timeline-turn-uturn-u2"]').length).toBe(1);
    expect(wrapper.find('[data-testid="timeline-process-head-uturn-u1"]').exists()).toBe(true);
    expect(wrapper.find('[data-testid="timeline-final-uturn-u1"]').exists()).toBe(true);
    expect(wrapper.find('[data-testid="timeline-final-empty-uturn-u2"]').exists()).toBe(true);
    expect(wrapper.find('[data-testid="timeline-final-empty-uturn-u2"]').text()).toContain('2 个工具');
  });

  it('keeps process boxes collapsed by default and lazy-loads on expand', async () => {
    const wrapper = mount(TimelineSummarySection, {
      props: { summaries: [makeSummary()] },
      global: { plugins: [ (await import('pinia')).createPinia() ] },
    });
    await nextTick();
    expect(wrapper.find('[data-testid="timeline-process-body-uturn-u1"]').exists()).toBe(false);
    expect(wrapper.find('[data-testid="timeline-process-placeholder-uturn-u1"]').exists()).toBe(true);
    await wrapper.find('[data-testid="timeline-process-head-uturn-u1"]').trigger('click');
    await nextTick();
    await new Promise((r) => setTimeout(r, 0));
    const body = wrapper.find('[data-testid="timeline-process-body-uturn-u1"]');
    expect(body.exists()).toBe(true);
    expect(wrapper.find('[data-testid="timeline-process-placeholder-uturn-u1"]').exists()).toBe(false);
  });
});

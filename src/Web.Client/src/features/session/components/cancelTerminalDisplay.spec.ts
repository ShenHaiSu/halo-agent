import { describe, expect, it } from 'vitest';
import { mount } from '@vue/test-utils';
import { nextTick } from 'vue';
import { createPinia, setActivePinia } from 'pinia';
import TimelineProcessBox from './TimelineProcessBox.vue';
import UserBubble from '@features/agent/components/userturn/UserBubble.vue';
import type { FlatTimelineTurn } from '@features/session/utils/timelineSummary';

function makeTurn(over: Partial<FlatTimelineTurn> = {}): FlatTimelineTurn {
  return {
    key: 'uturn-user-first',
    turnId: 'uturn-user-first',
    turnSeq: 1,
    userPreview: 'first question',
    userContent: 'first question',
    finalPreview: '',
    finalBlockId: null,
    blocks: 0,
    tools: 0,
    failed: 0,
    durationMs: 0,
    chars: 0,
    status: 'cancelled',
    lastRequestId: 'request-cancel',
    anchorKey: 'uturn-user-first',
    kind: 'turn',
    removedMessages: 0,
    ...over,
  };
}

async function flush(): Promise<void> {
  await nextTick();
  await new Promise((r) => setTimeout(r, 0));
  await nextTick();
}

describe('取消态气泡', () => {
  it('cancelled 显示已停止文案且可重发', async () => {
    setActivePinia(createPinia());
    const wrapper = mount(UserBubble, { props: { content: 'first question', pendingStatus: 'cancelled' } });
    await flush();
    expect(wrapper.find('[data-testid="pending-cancelled"]').exists()).toBe(true);
    expect(wrapper.text()).toContain('已停止·点击重发');
    await wrapper.find('[data-testid="pending-retry-btn"]').trigger('click');
    expect(wrapper.emitted('retry-pending')).toBeTruthy();
  });

  it('sending 仍显示发送中 spinner', async () => {
    setActivePinia(createPinia());
    const wrapper = mount(UserBubble, { props: { content: 'first question', pendingStatus: 'sending' } });
    await flush();
    expect(wrapper.find('[data-testid="pending-sending"]').exists()).toBe(true);
    expect(wrapper.find('[data-testid="pending-cancelled"]').exists()).toBe(false);
  });

  it('failed 仍显示发送失败文案', async () => {
    setActivePinia(createPinia());
    const wrapper = mount(UserBubble, { props: { content: 'first question', pendingStatus: 'failed' } });
    await flush();
    expect(wrapper.find('[data-testid="pending-failed"]').exists()).toBe(true);
    expect(wrapper.text()).toContain('发送失败·点击重发');
  });
});

describe('零块终态轮展示', () => {
  it('零块 cancelled 轮无过程盒与 0 工具文案，只留紧凑终态行', async () => {
    setActivePinia(createPinia());
    const wrapper = mount(TimelineProcessBox, {
      props: { turn: makeTurn() },
      global: { plugins: [createPinia()] },
    });
    await flush();
    expect(wrapper.find('[data-testid="timeline-process-box"]').exists()).toBe(false);
    expect(wrapper.find('[data-testid="timeline-final-empty-uturn-user-first"]').exists()).toBe(false);
    expect(wrapper.find('[data-testid="timeline-terminal-empty-uturn-user-first"]').exists()).toBe(true);
    expect(wrapper.find('[data-testid="timeline-terminal-empty-uturn-user-first"]').text()).toContain('已停止，未生成回复');
    expect(wrapper.find('.turn-user-slot').exists()).toBe(true);
  });

  it('零块 error 轮显示失败终态行', async () => {
    setActivePinia(createPinia());
    const wrapper = mount(TimelineProcessBox, {
      props: { turn: makeTurn({ status: 'error' }) },
      global: { plugins: [createPinia()] },
    });
    await flush();
    expect(wrapper.find('[data-testid="timeline-terminal-empty-uturn-user-first"]').text()).toContain('本轮失败，未生成回复');
  });

  it('有过程块的取消轮保留过程盒', async () => {
    setActivePinia(createPinia());
    const wrapper = mount(TimelineProcessBox, {
      props: { turn: makeTurn({ blocks: 2, tools: 1 }) },
      global: { plugins: [createPinia()] },
    });
    await flush();
    expect(wrapper.find('[data-testid="timeline-process-box"]').exists()).toBe(true);
    expect(wrapper.find('[data-testid="timeline-terminal-empty-uturn-user-first"]').exists()).toBe(false);
  });
});

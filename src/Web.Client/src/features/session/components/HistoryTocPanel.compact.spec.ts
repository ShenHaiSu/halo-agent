import { beforeEach, describe, expect, it, vi } from 'vitest';
import { mount } from '@vue/test-utils';
import { nextTick } from 'vue';
import { createPinia, setActivePinia } from 'pinia';
import type { TurnSummary } from '@shared/bridge/hostBridge';

const mocks = vi.hoisted(() => ({
  scrollToAnchor: vi.fn(async () => true),
  warning: vi.fn(),
}));

vi.mock('@/stores/workspaceStore', async () => {
  const { ref } = await import('vue');
  const { defineStore } = await import('pinia');
  return {
    useWorkspaceStore: defineStore('compact-toc-workspace', () => ({
      timelineSummaries: ref([
        {
          turnId: 'turn-compact-a',
          turnSeq: 1,
          userPreview: '上下文压缩',
          userContent: 'compact 摘要',
          kind: 'compact',
          removedMessages: 7,
          blocks: 1,
          tools: 1,
          finalBlockId: 'legacy-block',
          finalPreview: '不得显示',
        },
      ] as TurnSummary[]),
      loadingMessages: ref(false),
      activeSessionId: ref('session-compact'),
      isHistoryReadonly: ref(false),
    })),
  };
});

vi.mock('@/stores/subSessionStore', async () => {
  const { reactive } = await import('vue');
  return { useSubSessionStore: () => reactive({ mode: 'main', loading: false }) };
});

vi.mock('@features/agent/stores/useAgentStream', () => ({
  useAgentStream: () => ({ hasActiveTurn: false }),
}));

vi.mock('@/stores/uiStore', async () => {
  const { reactive } = await import('vue');
  return {
    useUiStore: () =>
      reactive({
        tocRole: 'all',
        hintShown: true,
        lockJump: vi.fn(),
        markHintShown: vi.fn(),
      }),
  };
});

vi.mock('@features/agent/composables/useTimelineNav', () => ({
  useTimelineNav: () => ({
    scrollToAnchor: mocks.scrollToAnchor,
    scrollToBottom: vi.fn(),
  }),
}));

vi.mock('naive-ui', () => ({
  useMessage: () => ({ warning: mocks.warning }),
}));

import HistoryTocPanel from './HistoryTocPanel.vue';

describe('HistoryTocPanel compact', () => {
  beforeEach(() => {
    setActivePinia(createPinia());
    mocks.scrollToAnchor.mockClear();
  });

  it('只显示一个居中 compact 项且 tooltip 为压缩', () => {
    const wrapper = mount(HistoryTocPanel, {
      global: { stubs: { NSelect: true, NButton: true } },
    });
    expect(wrapper.findAll('.toc-row--compact')).toHaveLength(1);
    const bubble = wrapper.find('[data-testid="toc-bubble-turn-compact-a"]');
    expect(bubble.attributes('title')).toContain('压缩');
    expect(bubble.text()).toContain('7 条');
    expect(wrapper.text()).not.toContain('过程内容');
    expect(wrapper.text()).not.toContain('本轮无正文');
    expect(wrapper.find('.toc-badge').exists()).toBe(false);
  });

  it('点击使用 compact turnId 跳转', async () => {
    const wrapper = mount(HistoryTocPanel, {
      global: { stubs: { NSelect: true, NButton: true } },
    });
    await wrapper.find('[data-testid="toc-bubble-turn-compact-a"]').trigger('click');
    await nextTick();
    expect(mocks.scrollToAnchor).toHaveBeenCalledWith('turn-compact-a', expect.objectContaining({ highlight: true }));
  });
});

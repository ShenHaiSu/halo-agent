import { beforeEach, describe, expect, it, vi } from 'vitest';
import { mount } from '@vue/test-utils';
import { nextTick } from 'vue';
import type { TurnSummary } from '@shared/bridge/hostBridge';

const mocks = vi.hoisted(() => ({
  getTurnBlockIds: vi.fn(async () => [] as string[]),
  getBlockContents: vi.fn(async () => ({ items: {}, missing: [] })),
  getToolResult: vi.fn(async () => null),
  registerAnchor: vi.fn(),
  unregisterAnchor: vi.fn(),
}));

vi.mock('@/stores/workspaceStore', () => ({
  useWorkspaceStore: () => ({
    getTurnBlockIds: mocks.getTurnBlockIds,
    getBlockContents: mocks.getBlockContents,
    getToolResult: mocks.getToolResult,
  }),
}));

vi.mock('@features/agent/composables/useTimelineNav', () => ({
  useTimelineNav: () => ({
    register: mocks.registerAnchor,
    unregister: mocks.unregisterAnchor,
  }),
}));

import TimelineSummarySection from './TimelineSummarySection.vue';
import { clearExpansion } from '../utils/timelineExpansion';

function summary(over: Partial<TurnSummary>): TurnSummary {
  return {
    turnId: 'turn-normal-a',
    turnSeq: 1,
    iterEnd: 1,
    userPreview: '普通提问',
    userContent: '普通提问全文',
    finalBlockId: null,
    finalPreview: '',
    blocks: 2,
    tools: 1,
    failed: 0,
    durationMs: 100,
    chars: 20,
    status: 'completed',
    lastRequestId: '',
    createdAtUtc: '',
    updatedAtUtc: '',
    anchorKey: 'turn-normal-a',
    ...over,
  } as TurnSummary;
}

const mixed = [
  summary({}),
  summary({
    turnId: 'turn-compact-a',
    turnSeq: 2,
    kind: 'compact',
    userPreview: '上下文压缩',
    userContent: '**compact 摘要**',
    blocks: 0,
    tools: 0,
    removedMessages: 9,
  }),
  summary({ turnId: 'turn-normal-b', turnSeq: 3, userPreview: '后续提问' }),
];

describe('TimelineSummarySection compact 路由', () => {
  beforeEach(() => {
    clearExpansion();
    vi.clearAllMocks();
  });

  it('compact 只渲染分隔线，不挂载普通轮槽位', async () => {
    const wrapper = mount(TimelineSummarySection, { props: { summaries: mixed } });
    await nextTick();

    expect(wrapper.find('[data-testid="compact-turn-turn-compact-a"]').exists()).toBe(true);
    expect(wrapper.find('[data-testid="compact-divider"]').exists()).toBe(true);
    expect(wrapper.find('[data-testid="timeline-turn-turn-compact-a"]').exists()).toBe(false);
    expect(wrapper.find('[data-testid="timeline-turn-turn-normal-a"]').exists()).toBe(true);
    expect(wrapper.find('[data-testid="timeline-turn-turn-normal-b"]').exists()).toBe(true);
  });

  it('compact 点击与展开不触发任何 block 请求', async () => {
    const wrapper = mount(TimelineSummarySection, { props: { summaries: mixed } });
    await nextTick();
    await wrapper.find('[data-testid="compact-pill"]').trigger('click');
    await nextTick();

    expect(wrapper.find('[data-testid="compact-summary"] strong').text()).toBe('compact 摘要');
    expect(mocks.getTurnBlockIds).not.toHaveBeenCalled();
    expect(mocks.getBlockContents).not.toHaveBeenCalled();
    expect(mocks.getToolResult).not.toHaveBeenCalled();
  });

  it('compact 只注册一个导航锚点', async () => {
    mount(TimelineSummarySection, { props: { summaries: mixed } });
    await nextTick();
    const keys = mocks.registerAnchor.mock.calls.map((call) => String(call[0]));
    expect(keys.filter((key) => key === 'turn-compact-a')).toHaveLength(1);
    expect(keys.some((key) => key.startsWith('turn-compact-a:'))).toBe(false);
  });

  it('compact 后接普通轮时 handoff 仍只作用于最后普通轮', async () => {
    const wrapper = mount(TimelineSummarySection, {
      props: { summaries: mixed, handoffActive: true, handoffFinalText: '交接正文' },
    });
    await nextTick();
    expect(wrapper.find('[data-testid="timeline-final-turn-normal-b"]').exists()).toBe(false);
    expect(wrapper.find('[data-testid="compact-divider"]').exists()).toBe(true);
  });
});

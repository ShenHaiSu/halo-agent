import { describe, expect, it, vi, beforeEach, afterEach } from 'vitest';
import { mount } from '@vue/test-utils';
import { nextTick } from 'vue';
import { createPinia, setActivePinia } from 'pinia';
import TimelineProcessBox from './TimelineProcessBox.vue';
import type { FlatTimelineTurn } from '@features/session/utils/timelineSummary';

const getBlockContentsMock = vi.fn();
const getTurnBlockIdsMock = vi.fn();
const getToolResultMock = vi.fn();

vi.mock('@/stores/workspaceStore', () => ({
  useWorkspaceStore: () => ({
    getBlockContents: getBlockContentsMock,
    getTurnBlockIds: getTurnBlockIdsMock,
    getToolResult: getToolResultMock,
  }),
}));

function makeTurn(over: Partial<FlatTimelineTurn> = {}): FlatTimelineTurn {
  return {
    key: 'uturn-u1',
    turnId: 'uturn-u1',
    turnSeq: 1,
    userPreview: '',
    userContent: '',
    finalPreview: '',
    finalBlockId: null,
    blocks: 2,
    tools: 0,
    failed: 0,
    durationMs: 10,
    chars: 8,
    status: 'completed',
    lastRequestId: 'req',
    anchorKey: 'uturn-u1',
    kind: 'turn',
    removedMessages: 0,
    ...over,
  };
}

async function tick(): Promise<void> {
  for (let i = 0; i < 5; i++) await nextTick();
  await Promise.resolve();
  for (let i = 0; i < 5; i++) await nextTick();
}

function mockProcessBlocks() {
  getTurnBlockIdsMock.mockResolvedValue(['b-1', 'b-2']);
  getBlockContentsMock.mockImplementation(async (ids: string[]) => {
    const items: Record<string, unknown> = {};
    for (const id of ids) {
      items[id] = { blockId: id, kind: 'text', tag: 'process', seq: 1, markdown: `body-${id}`, preview: '', truncated: false };
    }
    return { items, missing: [] };
  });
}

beforeEach(() => {
  setActivePinia(createPinia());
  vi.useFakeTimers();
  getBlockContentsMock.mockReset();
  getToolResultMock.mockReset();
  getTurnBlockIdsMock.mockReset();
  getTurnBlockIdsMock.mockResolvedValue([]);
});

afterEach(() => {
  vi.useRealTimers();
});

describe('TimelineProcessBox 折叠 3 秒 TTL', () => {
  it('展开后重复切换不重拉（缓存命中）', async () => {
    mockProcessBlocks();
    const wrapper = mount(TimelineProcessBox, {
      props: { turn: makeTurn() },
      global: { plugins: [createPinia()] },
    });
    await wrapper.find('[data-testid="timeline-process-head-uturn-u1"]').trigger('click');
    await tick();
    expect(getTurnBlockIdsMock).toHaveBeenCalledTimes(1);
    await wrapper.find('[data-testid="timeline-process-head-uturn-u1"]').trigger('click');
    await tick();
    await wrapper.find('[data-testid="timeline-process-head-uturn-u1"]').trigger('click');
    await tick();
    expect(getTurnBlockIdsMock).toHaveBeenCalledTimes(1);
  });

  it('折叠 <3 秒重开复用缓存、不重拉', async () => {
    mockProcessBlocks();
    const wrapper = mount(TimelineProcessBox, {
      props: { turn: makeTurn() },
      global: { plugins: [createPinia()] },
    });
    await wrapper.find('[data-testid="timeline-process-head-uturn-u1"]').trigger('click');
    await tick();
    expect(getTurnBlockIdsMock).toHaveBeenCalledTimes(1);
    await wrapper.find('[data-testid="timeline-process-head-uturn-u1"]').trigger('click');
    await tick();
    await vi.advanceTimersByTimeAsync(2000);
    await tick();
    await wrapper.find('[data-testid="timeline-process-head-uturn-u1"]').trigger('click');
    await tick();
    expect(getTurnBlockIdsMock).toHaveBeenCalledTimes(1);
  });

  it('折叠 >3 秒缓存清空、重开触发新请求', async () => {
    mockProcessBlocks();
    const wrapper = mount(TimelineProcessBox, {
      props: { turn: makeTurn() },
      global: { plugins: [createPinia()] },
    });
    await wrapper.find('[data-testid="timeline-process-head-uturn-u1"]').trigger('click');
    await tick();
    expect(getTurnBlockIdsMock).toHaveBeenCalledTimes(1);
    await wrapper.find('[data-testid="timeline-process-head-uturn-u1"]').trigger('click');
    await tick();
    await vi.advanceTimersByTimeAsync(3100);
    await tick();
    await wrapper.find('[data-testid="timeline-process-head-uturn-u1"]').trigger('click');
    await tick();
    expect(getTurnBlockIdsMock).toHaveBeenCalledTimes(2);
  });
});

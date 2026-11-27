/** 稳态正文收敛：交接种子只作首帧过渡，撤销后必须回读落盘正文且全程不闪空。 */
import { beforeEach, describe, expect, it, vi } from 'vitest';
import { mount } from '@vue/test-utils';
import { nextTick } from 'vue';
import { createPinia, setActivePinia } from 'pinia';
import TimelineProcessBox from './TimelineProcessBox.vue';
import MarkdownStreamBlocks from '@features/agent/components/MarkdownStreamBlocks.vue';
import type { FlatTimelineTurn } from '@features/session/utils/timelineSummary';

const getBlockContentsMock = vi.fn();
const getTurnBlockIdsMock = vi.fn();

vi.mock('@/stores/workspaceStore', () => ({
  useWorkspaceStore: () => ({
    getBlockContents: getBlockContentsMock,
    getTurnBlockIds: getTurnBlockIdsMock,
    getToolResult: vi.fn(),
    activeWorkspaceId: 'ws-alpha',
    activeSessionId: 'sess-alpha',
  }),
}));

const TURN_ID = 'turn-converge';
const FINAL_TESTID = `timeline-final-${TURN_ID}`;
const POLLUTED = '早期迭代正文\n# 污染末轮标题';
const CLEAN = '# 落盘末轮正文\nCLEAN_MARKER_OK';

function makeTurn(over: Partial<FlatTimelineTurn> = {}): FlatTimelineTurn {
  return {
    key: TURN_ID,
    turnId: TURN_ID,
    turnSeq: 1,
    userPreview: '',
    userContent: '',
    finalPreview: '结论：…',
    finalBlockId: 'blk-final',
    blocks: 2,
    tools: 1,
    failed: 0,
    durationMs: 800,
    chars: 42,
    status: 'completed',
    lastRequestId: 'req-alpha',
    anchorKey: TURN_ID,
    kind: 'turn',
    removedMessages: 0,
    ...over,
  };
}

function blockItem(blockId: string, markdown: string) {
  return { blockId, kind: 'text', tag: 'final', markdown, preview: '', truncated: false };
}

async function flush(): Promise<void> {
  await nextTick();
  await new Promise((r) => setTimeout(r, 0));
  await nextTick();
}

function deferred<T>() {
  let resolve!: (v: T) => void;
  let reject!: (e: unknown) => void;
  const promise = new Promise<T>((res, rej) => {
    resolve = res;
    reject = rej;
  });
  return { promise, resolve, reject };
}

beforeEach(() => {
  setActivePinia(createPinia());
  getBlockContentsMock.mockReset();
  getTurnBlockIdsMock.mockReset();
  getTurnBlockIdsMock.mockResolvedValue([]);
});

describe('交接 seed 生命周期闭环', () => {
  it('污染 seed 撤销后回读 finalBlockId，落盘正文覆盖污染值', async () => {
    getBlockContentsMock.mockResolvedValue({ items: { 'blk-final': blockItem('blk-final', CLEAN) }, missing: [] });
    const wrapper = mount(TimelineProcessBox, {
      props: { turn: makeTurn(), initialMarkdown: POLLUTED },
      global: { plugins: [createPinia()] },
    });
    await flush();
    expect(wrapper.find(`[data-testid="${FINAL_TESTID}"]`).text()).toContain('污染末轮标题');
    expect(getBlockContentsMock).not.toHaveBeenCalled();

    await wrapper.setProps({ initialMarkdown: '' });
    await flush();
    expect(getBlockContentsMock).toHaveBeenCalledWith(['blk-final']);
    const text = wrapper.find(`[data-testid="${FINAL_TESTID}"]`).text();
    expect(text).toContain('CLEAN_MARKER_OK');
    expect(text).not.toContain('早期迭代正文');
  });

  it('回读期间保留已有 markdown：不出现加载态、不闪 preview', async () => {
    const pending = deferred<{ items: Record<string, unknown>; missing: string[] }>();
    getBlockContentsMock.mockImplementation(() => pending.promise);
    const wrapper = mount(TimelineProcessBox, {
      props: { turn: makeTurn(), initialMarkdown: POLLUTED },
      global: { plugins: [createPinia()] },
    });
    await flush();
    await wrapper.setProps({ initialMarkdown: '' });
    await nextTick();
    expect(wrapper.find(`[data-testid="timeline-final-loading-${TURN_ID}"]`).exists()).toBe(false);
    expect(wrapper.find(`[data-testid="${FINAL_TESTID}"]`).text()).toContain('污染末轮标题');
    expect(wrapper.find(`[data-testid="${FINAL_TESTID}"]`).text()).not.toContain('结论：…');

    pending.resolve({ items: { 'blk-final': blockItem('blk-final', CLEAN) }, missing: [] });
    await flush();
    expect(wrapper.find(`[data-testid="${FINAL_TESTID}"]`).text()).toContain('CLEAN_MARKER_OK');
  });

  it('旧 finalBlockId 的迟到响应不覆盖新正文', async () => {
    const oldPending = deferred<{ items: Record<string, unknown>; missing: string[] }>();
    const newPending = deferred<{ items: Record<string, unknown>; missing: string[] }>();
    getBlockContentsMock.mockImplementation((ids: string[]) => (ids[0] === 'blk-old' ? oldPending.promise : newPending.promise));
    const wrapper = mount(TimelineProcessBox, {
      props: { turn: makeTurn({ finalBlockId: 'blk-old' }) },
      global: { plugins: [createPinia()] },
    });
    await nextTick();
    await wrapper.setProps({ turn: makeTurn({ finalBlockId: 'blk-final' }) });
    await nextTick();

    oldPending.resolve({ items: { 'blk-old': blockItem('blk-old', 'OLD_STALE_MARKER') }, missing: [] });
    await flush();
    expect(wrapper.find(`[data-testid="${FINAL_TESTID}"]`).text()).not.toContain('OLD_STALE_MARKER');

    newPending.resolve({ items: { 'blk-final': blockItem('blk-final', CLEAN) }, missing: [] });
    await flush();
    expect(wrapper.find(`[data-testid="${FINAL_TESTID}"]`).text()).toContain('CLEAN_MARKER_OK');
  });

  it('seed 后到仍同步铺满 markdown，不短路为 preview', async () => {
    const wrapper = mount(TimelineProcessBox, {
      props: { turn: makeTurn() },
      global: { plugins: [createPinia()] },
    });
    await flush();
    await wrapper.setProps({ initialMarkdown: POLLUTED });
    await nextTick();
    const finalBody = wrapper.find(`[data-testid="${FINAL_TESTID}"]`);
    expect(finalBody.text()).toContain('污染末轮标题');
    expect(finalBody.findComponent(MarkdownStreamBlocks).exists()).toBe(true);
    expect(getBlockContentsMock).toHaveBeenCalledTimes(1); // 仅挂载时对账
  });

  it('seed 撤销回读失败保留当前正文并给出非阻塞重试', async () => {
    getBlockContentsMock.mockRejectedValueOnce(new Error('bridge down'));
    getBlockContentsMock.mockResolvedValue({ items: { 'blk-final': blockItem('blk-final', CLEAN) }, missing: [] });
    const wrapper = mount(TimelineProcessBox, {
      props: { turn: makeTurn(), initialMarkdown: POLLUTED },
      global: { plugins: [createPinia()] },
    });
    await flush();
    await wrapper.setProps({ initialMarkdown: '' });
    await flush();
    expect(wrapper.find(`[data-testid="${FINAL_TESTID}"]`).text()).toContain('污染末轮标题');
    expect(wrapper.find(`[data-testid="timeline-final-reconcile-error-${TURN_ID}"]`).exists()).toBe(true);

    await wrapper.find(`[data-testid="timeline-final-retry-${TURN_ID}"]`).trigger('click');
    await flush();
    expect(wrapper.find(`[data-testid="${FINAL_TESTID}"]`).text()).toContain('CLEAN_MARKER_OK');
    expect(wrapper.find(`[data-testid="timeline-final-reconcile-error-${TURN_ID}"]`).exists()).toBe(false);
  });

  it('卸载后迟到的落盘响应不再更新状态', async () => {
    const pending = deferred<{ items: Record<string, unknown>; missing: string[] }>();
    getBlockContentsMock.mockImplementation(() => pending.promise);
    const warn = vi.spyOn(console, 'warn').mockImplementation(() => undefined);
    const wrapper = mount(TimelineProcessBox, {
      props: { turn: makeTurn() },
      global: { plugins: [createPinia()] },
    });
    await flush();
    wrapper.unmount();
    pending.resolve({ items: { 'blk-final': blockItem('blk-final', CLEAN) }, missing: [] });
    await flush();
    expect(getBlockContentsMock).toHaveBeenCalledTimes(1);
    expect(warn).not.toHaveBeenCalled();
    warn.mockRestore();
  });
});

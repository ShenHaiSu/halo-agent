import { describe, expect, it, vi, beforeEach } from 'vitest';
import { mount } from '@vue/test-utils';
import { nextTick } from 'vue';
import { createPinia, setActivePinia } from 'pinia';
import TimelineProcessBox from './TimelineProcessBox.vue';
import type { FlatTimelineTurn } from '@features/session/utils/timelineSummary';

const getBlockContentsMock = vi.fn();
const getTurnBlockIdsMock = vi.fn();

vi.mock('@/stores/workspaceStore', () => ({
  useWorkspaceStore: () => ({
    getBlockContents: getBlockContentsMock,
    getTurnBlockIds: getTurnBlockIdsMock,
  }),
}));

function makeTurn(over: Partial<FlatTimelineTurn> = {}): FlatTimelineTurn {
  return {
    key: 'uturn-u1',
    turnId: 'uturn-u1',
    turnSeq: 1,
    userPreview: '',
    finalPreview: '结论：…',
    finalBlockId: 'bfinal1',
    blocks: 2,
    tools: 1,
    failed: 0,
    durationMs: 800,
    chars: 42,
    status: 'completed',
    lastRequestId: 'req',
    anchorKey: 'uturn-u1',
    ...over,
  };
}

async function flush(): Promise<void> {
  await nextTick();
  await new Promise((r) => setTimeout(r, 0));
  await nextTick();
}

beforeEach(() => {
  setActivePinia(createPinia());
  getBlockContentsMock.mockReset();
  getTurnBlockIdsMock.mockResolvedValue([]);
});

describe('TimelineProcessBox Final 全文挂载（S2 欠账）', () => {
  it('有 finalBlockId 时渲染全量 markdown（非 60 字截断）', async () => {
    const full = '# 结论\n\n已经定位到空指针，详情如下：\n\n- 原因一：空引用\n- 原因二：越界\n\n```ts\nconst a = 1;\n```\n\n尾部全文标记-OK';
    getBlockContentsMock.mockResolvedValue({ items: { bfinal1: { blockId: 'bfinal1', kind: 'text', tag: 'final', markdown: full, preview: '结论：…', truncated: false } }, missing: [] });
    const wrapper = mount(TimelineProcessBox, {
      props: { turn: makeTurn() },
      global: { plugins: [createPinia()] },
    });
    await flush();
    // 仅含 finalBlockId，不含过程盒 ids
    expect(getBlockContentsMock).toHaveBeenCalledWith(['bfinal1']);
    expect(getTurnBlockIdsMock).not.toHaveBeenCalled();
    const finalEl = wrapper.find('[data-testid="timeline-final-uturn-u1"]');
    expect(finalEl.exists()).toBe(true);
    const html = finalEl.html();
    expect(html).toContain('尾部全文标记-OK');
    expect(html).not.toContain('timeline-final-empty');
    // markdown 结构：标题/列表/代码块至少命中其一
    expect(html.includes('<h1') || html.includes('<ul') || html.includes('<code')).toBe(true);
  });

  it('无 finalBlockId 纯工具轮保留空态', async () => {
    const wrapper = mount(TimelineProcessBox, {
      props: { turn: makeTurn({ finalBlockId: null, finalPreview: '' }) },
      global: { plugins: [createPinia()] },
    });
    await flush();
    expect(getBlockContentsMock).not.toHaveBeenCalled();
    const empty = wrapper.find('[data-testid="timeline-final-empty-uturn-u1"]');
    expect(empty.exists()).toBe(true);
    expect(empty.text()).toContain('1 个工具');
  });

  it('missing 显示已删除占位', async () => {
    getBlockContentsMock.mockResolvedValue({ items: {}, missing: ['bfinal1'] });
    const wrapper = mount(TimelineProcessBox, {
      props: { turn: makeTurn() },
      global: { plugins: [createPinia()] },
    });
    await flush();
    expect(wrapper.find('[data-testid="timeline-final-missing-uturn-u1"]').exists()).toBe(true);
  });

  it('加载失败可重试', async () => {
    getBlockContentsMock.mockRejectedValueOnce(new Error('boom'));
    const full = '# 结论\n\n重试后全文-OK';
    getBlockContentsMock.mockResolvedValue({ items: { bfinal1: { blockId: 'bfinal1', kind: 'text', tag: 'final', markdown: full, preview: '', truncated: false } }, missing: [] });
    const wrapper = mount(TimelineProcessBox, {
      props: { turn: makeTurn() },
      global: { plugins: [createPinia()] },
    });
    await flush();
    expect(wrapper.find('[data-testid="timeline-final-error-uturn-u1"]').exists()).toBe(true);
    await wrapper.find('[data-testid="timeline-final-retry-uturn-u1"]').trigger('click');
    await flush();
    expect(wrapper.find('[data-testid="timeline-final-uturn-u1"]').html()).toContain('重试后全文-OK');
  });
});

/** V2 摘要右键桥接：turnId → 完整投影条目 / 摘要原文 → 既有行级 emit 的单测。 */
import { beforeEach, describe, expect, it, vi } from 'vitest';
import { mount } from '@vue/test-utils';
import { nextTick } from 'vue';
import naive from 'naive-ui';
import TimelineView from '@features/agent/components/TimelineView.vue';
import { TimelineViewKey, timelineViewFallback } from '@features/agent/composables/timelineViewKey';
import type { TimelineEntry } from '@features/session/utils/turnGroup';
import type { TurnSummary } from '@shared/bridge/hostBridge';
import type { UserTurn } from '@features/session/utils/userTurn';

const { getBlockContentsMock } = vi.hoisted(() => ({ getBlockContentsMock: vi.fn() }));
vi.mock('@/stores/workspaceStore', () => ({
  useWorkspaceStore: () => ({ getBlockContents: getBlockContentsMock }),
}));

const summaryStub = {
  name: 'TimelineSummarySection',
  props: ['summaries'],
  emits: ['user-menu', 'assistant-menu'],
  template: '<div data-testid="summary-menu-stub"></div>',
};

const FULL_USER = '用户问题全文：请帮我重构这个模块并补充单元测试。';
const BLOCK_MARKDOWN = '# 正文标题\n\n这是从块存储拉取的 markdown 全文，不是截断预览。';

function makeSummary(): TurnSummary {
  return {
    turnId: 'uturn-u1',
    turnSeq: 1,
    iterEnd: 1,
    userPreview: '用户预览…',
    userContent: FULL_USER,
    finalBlockId: 'b1',
    finalPreview: '正文预览…',
    blocks: 1,
    tools: 0,
    failed: 0,
    durationMs: 100,
    chars: 10,
    status: 'completed',
    lastRequestId: 'req-1',
    createdAtUtc: '',
    updatedAtUtc: '',
    anchorKey: 'uturn-u1',
  } as TurnSummary;
}

function makeEntry(key = 'uturn-u1'): TimelineEntry {
  return {
    key,
    kind: 'user-turn',
    turn: { key, userId: 'u1', requestId: 'req-1', steps: [] } as unknown as UserTurn,
  };
}

function mountView(items: TimelineEntry[], summaries: TurnSummary[]) {
  const vm = {
    ...timelineViewFallback(),
    summaryList: summaries,
    hideLegacyHistory: summaries.length > 0,
  };
  return mount(TimelineView, {
    props: { items, isEmpty: false },
    global: {
      plugins: [naive],
      provide: { [TimelineViewKey]: vm },
      stubs: { TimelineSummarySection: summaryStub },
    },
  });
}

/** async 桥接：等微任务 + DOM 刷新后再断言 emit。 */
async function flush(): Promise<void> {
  await new Promise((r) => setTimeout(r, 0));
  await nextTick();
}

beforeEach(() => {
  getBlockContentsMock.mockReset();
  getBlockContentsMock.mockResolvedValue({ items: { b1: { markdown: BLOCK_MARKDOWN } }, missing: [] });
});

describe('V2 摘要区右键桥接', () => {
  it('投影条目命中：用户右键走完整轮次 handler（复制整轮全文）', async () => {
    const wrapper = mountView([makeEntry()], [makeSummary()]);
    const section = wrapper.findComponent({ name: 'TimelineSummarySection' });
    section.vm.$emit('user-menu', new MouseEvent('contextmenu'), 'uturn-u1');
    await flush();
    const emitted = wrapper.emitted('user-menu') as unknown[][];
    const item = emitted[0][1] as TimelineEntry;
    expect(item.kind).toBe('user-turn');
    expect((item.turn as UserTurn).userId).toBe('u1');
    expect(getBlockContentsMock).not.toHaveBeenCalled();
  });

  it('V2 空投影：用户气泡右键用摘要 userContent 全文填充复制文本', async () => {
    const wrapper = mountView([], [makeSummary()]);
    const section = wrapper.findComponent({ name: 'TimelineSummarySection' });
    section.vm.$emit('user-menu', new MouseEvent('contextmenu'), 'uturn-u1');
    await flush();
    const emitted = wrapper.emitted('user-menu') as unknown[][];
    const item = emitted[0][1] as TimelineEntry;
    expect(item.kind).toBe('user');
    // 复制文本非空且为用户原文全文（而非 userPreview 截断），menuCanCopy 才会为 true。
    expect(String(item.msg?.content || '')).toBe(FULL_USER);
  });

  it('V2 空投影：正文右键按 finalBlockId 拉块，用 markdown 全文填充复制文本', async () => {
    const wrapper = mountView([], [makeSummary()]);
    const section = wrapper.findComponent({ name: 'TimelineSummarySection' });
    section.vm.$emit('assistant-menu', new MouseEvent('contextmenu'), 'uturn-u1');
    await flush();
    expect(getBlockContentsMock).toHaveBeenCalledWith(['b1']);
    const emitted = wrapper.emitted('assistant-menu') as unknown[][];
    const item = emitted[0][1] as TimelineEntry;
    expect(item.kind).toBe('turn');
    // 复制的是块 markdown 全文（B 方案），不是 finalPreview 截断预览。
    expect(String(item.group?.content || '')).toBe(BLOCK_MARKDOWN);
    expect(String(item.group?.content || '')).not.toBe('正文预览…');
  });

  it('正文拉块失败/缺块时降级用 finalPreview，复制仍可用', async () => {
    getBlockContentsMock.mockResolvedValueOnce({ items: {}, missing: ['b1'] });
    const wrapper = mountView([], [makeSummary()]);
    const section = wrapper.findComponent({ name: 'TimelineSummarySection' });
    section.vm.$emit('assistant-menu', new MouseEvent('contextmenu'), 'uturn-u1');
    await flush();
    const emitted = wrapper.emitted('assistant-menu') as unknown[][];
    const item = emitted[0][1] as TimelineEntry;
    expect(String(item.group?.content || '')).toBe('正文预览…');
  });

  it('投影无匹配且摘要也无该轮时仍 emit 菜单事件（不吞事件）', async () => {
    const wrapper = mountView([], [makeSummary()]);
    const section = wrapper.findComponent({ name: 'TimelineSummarySection' });
    section.vm.$emit('user-menu', new MouseEvent('contextmenu'), 'uturn-ghost');
    await flush();
    const emitted = wrapper.emitted('user-menu') as unknown[][];
    expect(emitted).toBeTruthy();
    expect((emitted[0][1] as TimelineEntry).key).toBe('uturn-ghost');
  });

  it('单次右键只 emit 一次（无重复 handler）', async () => {
    const wrapper = mountView([makeEntry()], [makeSummary()]);
    const section = wrapper.findComponent({ name: 'TimelineSummarySection' });
    section.vm.$emit('user-menu', new MouseEvent('contextmenu'), 'uturn-u1');
    await flush();
    expect(wrapper.emitted('user-menu')).toHaveLength(1);
  });
});

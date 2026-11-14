import { describe, expect, it, vi, beforeEach } from 'vitest';
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
  getToolResultMock.mockReset();
  getTurnBlockIdsMock.mockReset();
  getTurnBlockIdsMock.mockResolvedValue([]);
});

describe('TimelineProcessBox Final 全文挂载（S2 欠账）', () => {
  it('有 finalBlockId 时渲染全量 markdown（非 60 字截断）', async () => {
    const full =
      '# 结论\n\n已经定位到空指针，详情如下：\n\n- 原因一：空引用\n- 原因二：越界\n\n```ts\nconst a = 1;\n```\n\n尾部全文标记-OK';
    getBlockContentsMock.mockResolvedValue({
      items: { bfinal1: { blockId: 'bfinal1', kind: 'text', tag: 'final', markdown: full, preview: '结论：…', truncated: false } },
      missing: [],
    });
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
    getBlockContentsMock.mockResolvedValue({
      items: { bfinal1: { blockId: 'bfinal1', kind: 'text', tag: 'final', markdown: full, preview: '', truncated: false } },
      missing: [],
    });
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
describe('TimelineProcessBox history tool cards', () => {
  const toolCardStub = {
    props: ['card'],
    template: '<div data-testid="tool-call-card" :data-tool="card.functionName" :data-status="card.status">{{ card.functionName }}</div>',
  };

  function mockProcessBlocks() {
    getTurnBlockIdsMock.mockResolvedValue(['b-call-1', 'b-result-1']);
    getBlockContentsMock.mockImplementation(async (ids: string[]) => {
      const items: Record<string, unknown> = {};
      for (const id of ids) {
        if (id === 'bfinal1') {
          items[id] = { blockId: id, kind: 'text', tag: 'final', markdown: 'final', preview: '', truncated: false };
        } else if (id === 'b-call-1') {
          items[id] = {
            blockId: id,
            kind: 'tool_call',
            tag: 'process',
            callId: 'call-1',
            functionName: 'exec_command',
            seq: 1,
            markdown: '{"command":"echo hi"}',
            argsJson: '{"command":"echo hi"}',
            preview: '',
            truncated: false,
          };
        } else if (id === 'b-result-1') {
          items[id] = {
            blockId: id,
            kind: 'tool_result',
            tag: 'process',
            callId: 'call-1',
            functionName: 'exec_command',
            seq: 2,
            resultText: 'hi',
            resultRef: null,
            preview: '',
            truncated: false,
          };
        }
      }
      return { items, missing: [] };
    });
  }

  it('expands to real tool card with function name', async () => {
    mockProcessBlocks();
    const wrapper = mount(TimelineProcessBox, {
      props: { turn: makeTurn({ finalBlockId: null, finalPreview: '' }) },
      global: { plugins: [createPinia()], stubs: { ToolCallCard: toolCardStub } },
    });
    await wrapper.find('[data-testid="timeline-process-head-uturn-u1"]').trigger('click');
    await flush();
    const toolWrap = wrapper.find('[data-testid="timeline-tool-call-call-1"]');
    expect(toolWrap.exists()).toBe(true);
    expect(toolWrap.html()).toContain('data-tool="exec_command"');
    expect(toolWrap.text()).toBe('exec_command');
  });

  it('does not call getToolResult before card expand, calls once after', async () => {
    getTurnBlockIdsMock.mockResolvedValue(['b-call-2', 'b-result-2']);
    getBlockContentsMock.mockImplementation(async (ids: string[]) => {
      const items: Record<string, unknown> = {};
      for (const id of ids) {
        if (id === 'b-call-2') {
          items[id] = {
            blockId: id,
            kind: 'tool_call',
            tag: 'process',
            callId: 'call-2',
            functionName: 'read_file',
            seq: 1,
            argsJson: '{"path":"a.txt"}',
            markdown: '{"path":"a.txt"}',
            preview: '',
            truncated: false,
          };
        } else if (id === 'b-result-2') {
          items[id] = {
            blockId: id,
            kind: 'tool_result',
            tag: 'process',
            callId: 'call-2',
            functionName: 'read_file',
            seq: 2,
            resultText: '',
            resultRef: 'fileRef:tool-outputs/big.txt',
            preview: '',
            truncated: true,
          };
        }
      }
      return { items, missing: [] };
    });
    getToolResultMock.mockResolvedValue({ resultText: 'big-content', resultRef: null, functionName: 'read_file' });
    const wrapper = mount(TimelineProcessBox, {
      props: { turn: makeTurn({ finalBlockId: null, finalPreview: '' }) },
      global: { plugins: [createPinia()], stubs: { ToolCallCard: toolCardStub } },
    });
    await wrapper.find('[data-testid="timeline-process-head-uturn-u1"]').trigger('click');
    await flush();
    expect(wrapper.find('[data-testid="timeline-tool-call-call-2"]').exists()).toBe(true);
    expect(getToolResultMock).not.toHaveBeenCalled();
    await wrapper.find('[data-testid="timeline-tool-call-call-2"]').trigger('click');
    await flush();
    expect(getToolResultMock).toHaveBeenCalledTimes(1);
    expect(getToolResultMock).toHaveBeenCalledWith('uturn-u1', 'call-2');
    await wrapper.find('[data-testid="timeline-tool-call-call-2"]').trigger('click');
    await flush();
    expect(getToolResultMock).toHaveBeenCalledTimes(1);
  });

  it('applies lazy getToolResult success bit to card status', async () => {
    getTurnBlockIdsMock.mockResolvedValue(['b-call-3', 'b-result-3']);
    getBlockContentsMock.mockImplementation(async (ids: string[]) => {
      const items: Record<string, unknown> = {};
      for (const id of ids) {
        if (id === 'b-call-3') {
          items[id] = {
            blockId: id,
            kind: 'tool_call',
            tag: 'process',
            callId: 'call-3',
            functionName: 'read_file',
            seq: 1,
            argsJson: '{"path":"a.txt"}',
            markdown: '{"path":"a.txt"}',
            preview: '',
            truncated: false,
          };
        } else if (id === 'b-result-3') {
          items[id] = {
            blockId: id,
            kind: 'tool_result',
            tag: 'process',
            callId: 'call-3',
            functionName: 'read_file',
            seq: 2,
            resultText: '',
            resultRef: 'fileRef:tool-outputs/big.txt',
            preview: '',
            truncated: true,
          };
        }
      }
      return { items, missing: [] };
    });
    getToolResultMock.mockResolvedValue({ resultText: 'big-content', resultRef: null, functionName: 'read_file', success: false });
    const wrapper = mount(TimelineProcessBox, {
      props: { turn: makeTurn({ finalBlockId: null, finalPreview: '' }) },
      global: { plugins: [createPinia()], stubs: { ToolCallCard: toolCardStub } },
    });
    await wrapper.find('[data-testid="timeline-process-head-uturn-u1"]').trigger('click');
    await flush();
    expect(wrapper.find('[data-testid="tool-call-card"]').attributes('data-status')).toBe('done');
    await wrapper.find('[data-testid="timeline-tool-call-call-3"]').trigger('click');
    await flush();
    expect(getToolResultMock).toHaveBeenCalledWith('uturn-u1', 'call-3');
    expect(wrapper.find('[data-testid="tool-call-card"]').attributes('data-status')).toBe('error');
  });
});

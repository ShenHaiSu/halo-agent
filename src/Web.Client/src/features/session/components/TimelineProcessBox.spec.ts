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
    userContent: '',
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
  it('keeps args from call block even when result block carries no args', async () => {
    getTurnBlockIdsMock.mockResolvedValue(['b-call-4', 'b-result-4']);
    getBlockContentsMock.mockImplementation(async () => ({
      items: {
        'b-call-4': {
          blockId: 'b-call-4',
          kind: 'tool_call',
          tag: 'process',
          callId: 'call-4',
          functionName: 'exec_command',
          argsJson: '{"command":"echo hi"}',
          markdown: '{"command":"echo hi"}',
          preview: '',
          truncated: false,
        },
        'b-result-4': {
          blockId: 'b-result-4',
          kind: 'tool_result',
          tag: 'process',
          callId: 'call-4',
          resultText: 'hi',
          resultRef: null,
          preview: '',
          truncated: false,
        },
      },
      missing: [],
    }));
    const fullStub = {
      props: ['card'],
      template:
        '<div data-testid="tool-call-card" :data-tool="card.functionName" :data-args="card.argsJson" :data-result="card.resultText">{{ card.functionName }}</div>',
    };
    const wrapper = mount(TimelineProcessBox, {
      props: { turn: makeTurn({ finalBlockId: null, finalPreview: '' }) },
      global: { plugins: [createPinia()], stubs: { ToolCallCard: fullStub } },
    });
    await wrapper.find('[data-testid="timeline-process-head-uturn-u1"]').trigger('click');
    await flush();
    const card = wrapper.find('[data-testid="tool-call-card"]');
    expect(card.exists()).toBe(true);
    expect(card.attributes('data-tool')).toBe('exec_command');
    expect(card.attributes('data-args')).toBe('{"command":"echo hi"}');
    expect(card.attributes('data-result')).toBe('hi');
  });

  it('supports legacy tool kind for compatibility', async () => {
    getTurnBlockIdsMock.mockResolvedValue(['b-call-legacy', 'b-result-legacy']);
    getBlockContentsMock.mockImplementation(async () => ({
      items: {
        'b-call-legacy': {
          blockId: 'b-call-legacy',
          kind: 'tool',
          tag: 'process',
          callId: 'call-legacy',
          functionName: 'read_file',
          argsJson: '{"path":"a.txt"}',
          markdown: '{"path":"a.txt"}',
          preview: '',
          truncated: false,
        },
        'b-result-legacy': {
          blockId: 'b-result-legacy',
          kind: 'tool_result',
          tag: 'process',
          callId: 'call-legacy',
          resultText: 'ok',
          resultRef: null,
          preview: '',
          truncated: false,
        },
      },
      missing: [],
    }));
    const wrapper = mount(TimelineProcessBox, {
      props: { turn: makeTurn({ finalBlockId: null, finalPreview: '' }) },
      global: { plugins: [createPinia()], stubs: { ToolCallCard: toolCardStub } },
    });
    await wrapper.find('[data-testid="timeline-process-head-uturn-u1"]').trigger('click');
    await flush();
    expect(wrapper.find('[data-testid="timeline-tool-call-call-legacy"]').exists()).toBe(true);
  });

  it('orphan tool_result without call block builds no card', async () => {
    getTurnBlockIdsMock.mockResolvedValue(['b-result-orphan']);
    getBlockContentsMock.mockImplementation(async () => ({
      items: {
        'b-result-orphan': {
          blockId: 'b-result-orphan',
          kind: 'tool_result',
          tag: 'process',
          callId: 'call-orphan',
          resultText: 'orphan',
          resultRef: null,
          preview: '',
          truncated: false,
        },
      },
      missing: [],
    }));
    const wrapper = mount(TimelineProcessBox, {
      props: { turn: makeTurn({ finalBlockId: null, finalPreview: '' }) },
      global: { plugins: [createPinia()], stubs: { ToolCallCard: toolCardStub } },
    });
    await wrapper.find('[data-testid="timeline-process-head-uturn-u1"]').trigger('click');
    await flush();
    expect(wrapper.find('[data-testid="timeline-tool-call-call-orphan"]').exists()).toBe(false);
    expect(wrapper.find('[data-testid="tool-call-card"]').exists()).toBe(false);
  });
});

describe('TimelineProcessBox user slot', () => {
  it('renders full user content in history instead of 15-char preview', async () => {
    const full = '请帮我分析报错日志并给出修复步骤：' + 'a'.repeat(100) + '尾部全文标记-USER-OK';
    const wrapper = mount(TimelineProcessBox, {
      props: { turn: makeTurn({ userPreview: full.slice(0, 15), userContent: full, finalBlockId: null, finalPreview: '' }) },
      global: { plugins: [createPinia()] },
    });
    await flush();
    const slot = wrapper.find('.turn-user-slot');
    expect(slot.exists()).toBe(true);
    expect(slot.text()).toContain('尾部全文标记-USER-OK');
    expect(slot.text().length).toBeGreaterThan(100);
  });

  it('falls back to preview when content is empty', async () => {
    const wrapper = mount(TimelineProcessBox, {
      props: { turn: makeTurn({ userPreview: 'hello', userContent: '', finalBlockId: null, finalPreview: '' }) },
      global: { plugins: [createPinia()] },
    });
    await flush();
    expect(wrapper.find('.turn-user-slot').text()).toContain('hello');
  });
});

describe('盒内渲染序', () => {
  const toolCardStub = {
    props: ['card'],
    template: '<div data-testid="tool-call-card" :data-tool="card.functionName" :data-status="card.status">{{ card.functionName }}</div>',
  };

  it('按块序交织渲染思考正文与工具卡而不是按类型分组', async () => {
    getTurnBlockIdsMock.mockResolvedValue(['b-rsn', 'b-txt', 'b-call', 'b-res', 'b-txt2']);
    getBlockContentsMock.mockImplementation(async (ids: string[]) => {
      const items: Record<string, unknown> = {};
      for (const id of ids) {
        items[id] =
          id === 'b-rsn'
            ? { blockId: id, kind: 'reasoning', tag: 'process', seq: 1, markdown: '思考', preview: '', truncated: false }
            : id === 'b-txt'
              ? { blockId: id, kind: 'text', tag: 'process', seq: 2, markdown: '过程正文', preview: '', truncated: false }
              : id === 'b-call'
                ? {
                    blockId: id,
                    kind: 'tool_call',
                    tag: 'process',
                    seq: 3,
                    callId: 'c1',
                    functionName: 'read_file',
                    argsJson: '{}',
                    markdown: '{}',
                    preview: '',
                    truncated: false,
                  }
                : id === 'b-res'
                  ? {
                      blockId: id,
                      kind: 'tool_result',
                      tag: 'process',
                      seq: 4,
                      callId: 'c1',
                      functionName: 'read_file',
                      resultText: 'ok',
                      resultRef: null,
                      preview: '',
                      truncated: false,
                    }
                  : { blockId: id, kind: 'text', tag: 'process', seq: 5, markdown: '收尾', preview: '', truncated: false };
      }
      return { items, missing: [] };
    });

    const wrapper = mount(TimelineProcessBox, {
      props: { turn: makeTurn({ finalBlockId: null, finalPreview: '' }) },
      global: { plugins: [createPinia()], stubs: { ToolCallCard: toolCardStub } },
    });
    await wrapper.find('[data-testid="timeline-process-head-uturn-u1"]').trigger('click');
    await flush();

    const kinds = wrapper.findAll('[data-hi-kind]').map((n) => n.attributes('data-hi-kind'));
    expect(kinds).toEqual(['reasoning', 'text', 'tool', 'text']);
  });

  it('思考节点在文档序中位于工具卡之前', async () => {
    getTurnBlockIdsMock.mockResolvedValue(['b-rsn', 'b-txt', 'b-call', 'b-res', 'b-txt2']);
    getBlockContentsMock.mockImplementation(async (ids: string[]) => {
      const items: Record<string, unknown> = {};
      for (const id of ids) {
        items[id] =
          id === 'b-rsn'
            ? { blockId: id, kind: 'reasoning', tag: 'process', seq: 1, markdown: '思考', preview: '', truncated: false }
            : id === 'b-txt'
              ? { blockId: id, kind: 'text', tag: 'process', seq: 2, markdown: '过程正文', preview: '', truncated: false }
              : id === 'b-call'
                ? {
                    blockId: id,
                    kind: 'tool_call',
                    tag: 'process',
                    seq: 3,
                    callId: 'c1',
                    functionName: 'read_file',
                    argsJson: '{}',
                    markdown: '{}',
                    preview: '',
                    truncated: false,
                  }
                : id === 'b-res'
                  ? {
                      blockId: id,
                      kind: 'tool_result',
                      tag: 'process',
                      seq: 4,
                      callId: 'c1',
                      functionName: 'read_file',
                      resultText: 'ok',
                      resultRef: null,
                      preview: '',
                      truncated: false,
                    }
                  : { blockId: id, kind: 'text', tag: 'process', seq: 5, markdown: '收尾', preview: '', truncated: false };
      }
      return { items, missing: [] };
    });

    const wrapper = mount(TimelineProcessBox, {
      props: { turn: makeTurn({ finalBlockId: null, finalPreview: '' }) },
      global: { plugins: [createPinia()], stubs: { ToolCallCard: toolCardStub } },
    });
    await wrapper.find('[data-testid="timeline-process-head-uturn-u1"]').trigger('click');
    await flush();

    const els = wrapper.findAll('[data-hi-kind]');
    expect(els.length).toBe(4);
    const pos = els[0].element.compareDocumentPosition(els[2].element);
    expect(pos & Node.DOCUMENT_POSITION_FOLLOWING).toBeTruthy();
  });
});

describe('TimelineProcessBox 右键菜单事件', () => {
  it('用户气泡右键 emit user-menu 并带 turnId', async () => {
    const wrapper = mount(TimelineProcessBox, {
      props: { turn: makeTurn({ userContent: '这是用户消息' }) },
      global: { plugins: [createPinia()] },
    });
    await flush();
    const bubble = wrapper.find('.turn-user-slot > div');
    expect(bubble.exists()).toBe(true);
    await bubble.trigger('contextmenu');
    const events = wrapper.emitted('user-menu');
    expect(events).toBeTruthy();
    expect((events![0] as unknown[])[1]).toBe('uturn-u1');
  });

  it('最终正文（markdown 形态）右键 emit assistant-menu', async () => {
    getBlockContentsMock.mockResolvedValue({
      items: {
        bfinal1: { blockId: 'bfinal1', kind: 'text', tag: 'final', markdown: '```js\nconst a=1;\n```', preview: '', truncated: false },
      },
      missing: [],
    });
    const wrapper = mount(TimelineProcessBox, {
      props: { turn: makeTurn() },
      global: { plugins: [createPinia()] },
    });
    await flush();
    const finalEl = wrapper.find('[data-testid="timeline-final-uturn-u1"]');
    expect(finalEl.exists()).toBe(true);
    await finalEl.trigger('contextmenu');
    const events = wrapper.emitted('assistant-menu');
    expect(events).toBeTruthy();
    expect((events![0] as unknown[])[1]).toBe('uturn-u1');
  });

  it('最终正文（preview 形态）右键 emit assistant-menu', async () => {
    const wrapper = mount(TimelineProcessBox, {
      props: { turn: makeTurn({ finalBlockId: null, finalPreview: '预览文本' }) },
      global: { plugins: [createPinia()] },
    });
    await flush();
    const finalEl = wrapper.find('[data-testid="timeline-final-uturn-u1"]');
    expect(finalEl.exists()).toBe(true);
    await finalEl.trigger('contextmenu');
    const events = wrapper.emitted('assistant-menu');
    expect(events).toBeTruthy();
    expect((events![0] as unknown[])[1]).toBe('uturn-u1');
  });

  it('无正文态右键仍 emit assistant-menu', async () => {
    const wrapper = mount(TimelineProcessBox, {
      props: { turn: makeTurn({ finalBlockId: null, finalPreview: '' }) },
      global: { plugins: [createPinia()] },
    });
    await flush();
    await wrapper.find('[data-testid="timeline-final-empty-uturn-u1"]').trigger('contextmenu');
    expect(wrapper.emitted('assistant-menu')).toBeTruthy();
  });
});

describe('交接期稳态正文预取与单份', () => {
  it('传入 initialMarkdown 时首帧即全文 markdown，不闪「全文加载中」纯文本', async () => {
    // 后端取块故意返回空：种子文本必须自己撑起首帧，不依赖异步请求。
    getBlockContentsMock.mockResolvedValue({ items: {}, missing: ['bfinal1'] });
    const seeded = '# 交接种子\n\n- 第一条\n- 第二条';
    const wrapper = mount(TimelineProcessBox, {
      props: { turn: makeTurn(), initialMarkdown: seeded },
      global: { plugins: [createPinia()] },
    });
    await nextTick();
    expect(wrapper.find('[data-testid="timeline-final-loading-uturn-u1"]').exists()).toBe(false);
    const block = wrapper.find('[data-testid="timeline-final-uturn-u1"] .stream-block');
    expect(block.exists()).toBe(true);
    expect(block.text()).toContain('交接种子');
    expect(getBlockContentsMock).not.toHaveBeenCalledWith(['bfinal1']);
  });

  it('种子后到（盒先挂载、交接窗后开）也能同步落位', async () => {
    // 取块悬而未决：模拟交接窗开启时异步请求还没回来，种子必须立刻接管。
    getBlockContentsMock.mockReturnValue(new Promise(() => undefined));
    const wrapper = mount(TimelineProcessBox, {
      props: { turn: makeTurn(), initialMarkdown: '' },
      global: { plugins: [createPinia()] },
    });
    await flush();
    expect(wrapper.find('[data-testid="timeline-final-loading-uturn-u1"]').exists()).toBe(true);
    await wrapper.setProps({ initialMarkdown: '# 后到种子' });
    await nextTick();
    expect(wrapper.find('[data-testid="timeline-final-loading-uturn-u1"]').exists()).toBe(false);
    expect(wrapper.find('[data-testid="timeline-final-uturn-u1"] .stream-block').text()).toContain('后到种子');
  });

  it('hideFinal 时整段 final 槽不渲染，正文唯一实例交给 Live 容器', async () => {
    const wrapper = mount(TimelineProcessBox, {
      props: { turn: makeTurn(), hideFinal: true, initialMarkdown: '不应显示' },
      global: { plugins: [createPinia()] },
    });
    await nextTick();
    expect(wrapper.findAll('[data-testid^="timeline-final-"]').length).toBe(0);
    expect(wrapper.find('.turn-final-slot').exists()).toBe(false);
  });
});

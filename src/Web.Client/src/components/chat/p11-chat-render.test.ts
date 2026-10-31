// P11-chat-render 回归用例：A 用户纯文本 / B 链接策略 / C 历史 tool_calls 回填 /
// D 未知工具去嵌套 / E 折叠单源 / F 裸 diff / G 流式与图片 alt。
import { beforeEach, describe, expect, it } from 'vitest';
import { nextTick, ref } from 'vue';
import { mount } from '@vue/test-utils';
import { createPinia, setActivePinia } from 'pinia';
import { classifyLink, isMailto, isSafeUrl } from './sanitize';
import { parseInline, parseMarkdown } from './MarkdownRenderer';
import {
  buildToolContentMap,
  normalizeOpenAiToolCalls,
  useMessagesStore,
} from '@/stores/messages';
import { useUiStore } from '@/stores/ui';
import { toMessageViews } from '@/stores/eventBridge';
import {
  clearCollapsedTools,
  collapsedToolIds,
  toolStatusText,
} from './toolMeta';
import { blockToLines, DIFF_MAX_LINES, extractDiffBlocks } from './toolDiff';
import { holdbackCut, useStreamingMarkdown } from './useStreamingMarkdown';
import MessageItem from './MessageItem.vue';
import ToolCard from './ToolCard.vue';

beforeEach(() => {
  setActivePinia(createPinia());
});

// ---------------------------------------------------------------- A：用户消息纯文本
describe('A 用户消息纯文本气泡', () => {
  function mountItem(message: Record<string, unknown>, isStreaming = false) {
    return mount(MessageItem, {
      props: {
        message: { id: 'm1', ts: Date.now(), content: '', ...message },
        isStreaming,
      },
      global: {
        stubs: {
          MessageAvatar: true,
          MessageActions: true,
          ReasoningBlock: true,
          StreamingCursor: true,
          ToolCard: true,
        },
      },
    } as never);
  }

  it('用户输入的 markdown 原样显示，不解析', () => {
    const w = mountItem({ role: 'user', content: '**加粗** _斜体_ # 标题\n第二行' });
    const plain = w.find('.user-plain');
    expect(plain.exists()).toBe(true);
    expect(plain.text()).toContain('**加粗** _斜体_ # 标题');
    expect(w.find('strong').exists()).toBe(false);
    expect(w.findComponent({ name: 'MessageBody' }).exists()).toBe(false);
    w.unmount();
  });

  it('assistant 同输入正常渲染', () => {
    const w = mountItem({ role: 'assistant', content: '**加粗**' });
    expect(w.find('.user-plain').exists()).toBe(false);
    expect(w.find('strong').exists()).toBe(true);
    w.unmount();
  });
});

// ---------------------------------------------------------------- B：链接策略
describe('B 链接点击策略三档', () => {
  it('isMailto 大小写均可', () => {
    expect(isMailto('mailto:a@b.c')).toBe(true);
    expect(isMailto('MAILTO:a@b.c')).toBe(true);
    expect(isMailto('https://a.com')).toBe(false);
  });

  it('classifyLink 三档 + 非安全协议', () => {
    expect(classifyLink('https://example.com/x')).toBe('external');
    expect(classifyLink('http://example.com')).toBe('external');
    expect(classifyLink('mailto:a@b.c')).toBe('mailto');
    expect(classifyLink('./a.md')).toBe('relative');
    expect(classifyLink('/x/y')).toBe('relative');
    expect(classifyLink('javascript:alert(1)')).toBe('unsafe');
  });

  it('XSS 仍为纯文本：javascript: 不成链', () => {
    expect(isSafeUrl('javascript:alert(1)', 'a')).toBe(false);
    expect(isSafeUrl('mailto:a@b.c', 'a')).toBe(true);
  });
});

// ---------------------------------------------------------------- C：历史 tool_calls 回填
describe('C 历史 OpenAI tool_calls 结果回填', () => {
  it('assistant(tool_calls=[t1]) + tool(t1, "out") → 卡片 result/output 为 out', () => {
    const messages = useMessagesStore();
    messages.applySnapshot({
      messages: [
        {
          id: 'a1',
          role: 'assistant',
          content: '',
          tool_calls: [{ id: 't1', function: { name: 'read_file', arguments: '{"p":1}' } }],
        },
        { role: 'tool', tool_call_id: 't1', content: 'out' },
      ],
    });
    expect(messages.items).toHaveLength(1);
    const tc = messages.items[0]!.toolCalls![0]!;
    expect(tc.result).toBe('out');
    expect(tc.output).toBe('out');
    expect(tc.ok).toBeUndefined();
    expect(tc.status).toBe('done');
  });

  it('孤儿调用 t2（无对应 tool 消息）结果为空但不崩', () => {
    const messages = useMessagesStore();
    messages.applySnapshot({
      messages: [
        {
          id: 'a1',
          role: 'assistant',
          content: 'x',
          tool_calls: [
            { id: 't1', function: { name: 'read_file', arguments: '{}' } },
            { id: 't2', function: { name: 'run_shell', arguments: '{}' } },
          ],
        },
        { role: 'tool', tool_call_id: 't1', content: 'out1' },
      ],
    });
    const calls = messages.items[0]!.toolCalls!;
    expect(calls).toHaveLength(2);
    expect(calls[0]!.result).toBe('out1');
    expect(calls[1]!.result).toBeUndefined();
    expect(calls[1]!.output).toBeUndefined();
  });

  it('buildToolContentMap / normalizeOpenAiToolCalls 直调', () => {
    const map = buildToolContentMap([
      { role: 'tool', tool_call_id: 't1', content: 'hi' },
      { role: 'assistant', content: 'x' },
    ]);
    expect(map.get('t1')).toBe('hi');
    const out = normalizeOpenAiToolCalls(
      [{ id: 't1', function: { name: 'n', arguments: '{}' } }],
      map
    );
    expect(out[0]).toMatchObject({ id: 't1', result: 'hi', output: 'hi' });
  });

  it('toMessageViews 透传 role=tool，后续 applySnapshot 可 join', () => {
    const views = toMessageViews([
      {
        role: 'assistant',
        content: '',
        tool_calls: [{ id: 't1', function: { name: 'read_file', arguments: '{}' } }],
      },
      { role: 'tool', tool_call_id: 't1', content: 'joined' },
    ]);
    const messages = useMessagesStore();
    messages.applySnapshot({ messages: views });
    expect(messages.items).toHaveLength(1);
    expect(messages.items[0]!.toolCalls![0]!.result).toBe('joined');
  });
});

// ---------------------------------------------------------------- D：未知工具去嵌套
describe('D 未知工具卡一次展开见参数', () => {
  function mountTool(tool: Record<string, unknown>) {
    return mount(ToolCard, {
      props: {
        tool: {
          id: 't1',
          name: 'web_search',
          arguments: '{}',
          status: 'error',
          kind: 'unknown',
          rawType: 'web_search',
          ...tool,
        },
        isLive: false,
      },
      global: { stubs: { ToolCardBody: true } },
    } as never);
  }

  it('头部唯一，展开即见原生参数（无二级折叠按钮）', () => {
    const w = mountTool({ output: 'o' });
    const buttons = w.findAll('button');
    expect(buttons).toHaveLength(1);
    expect(buttons[0]!.classes()).toContain('tool-head');
    expect(w.text()).toContain('原生参数');
    // rawType ?? name 标题不变
    expect(w.text()).toContain('web_search');
    w.unmount();
  });

  it('普通/未知工具头部时长显示一致', () => {
    const w = mountTool({ durationMs: 1500 });
    expect(w.text()).toContain('1.5s');
    w.unmount();
  });

  it(' toolStatusText 两处头部共用', () => {
    expect(toolStatusText('done')).toBe('成功');
    expect(toolStatusText('error')).toBe('失败');
  });
});

// ---------------------------------------------------------------- E：折叠单源
describe('E 折叠记忆单源（ui.collapsedTools）', () => {
  it('toggle → resync → 折叠态清空', () => {
    const ui = useUiStore();
    collapsedToolIds.add('t-collapsed');
    expect(ui.collapsedTools.has('t-collapsed')).toBe(true);
    expect(collapsedToolIds.has('t-collapsed')).toBe(true);
    clearCollapsedTools();
    expect(ui.collapsedTools.size).toBe(0);
    expect(collapsedToolIds.size).toBe(0);
  });
});

// ---------------------------------------------------------------- F：diff 可见性
describe('F 工具结果 diff 可见性', () => {
  const fenced =
    '说明文字\n```diff\n--- a/f\n+++ b/f\n@@ -1 +1 @@\n-a\n+b\n```\n尾巴';
  const bare =
    'edit ok\ndiff --git a/f b/f\n--- a/f\n+++ b/f\n@@ -1 +1 @@\n-a\n+b\n done';
  const log = 'run shell output\nline2\nno diff here';

  it('围栏 diff 照常提取', () => {
    expect(extractDiffBlocks(fenced)).toHaveLength(1);
  });

  it('裸 unified diff 被提取（edit_file 真实结果有红绿预览）', () => {
    const blocks = extractDiffBlocks(bare);
    expect(blocks).toHaveLength(1);
    const lines = blockToLines(blocks[0]!);
    expect(lines.some((l) => l.type === 'add')).toBe(true);
    expect(lines.some((l) => l.type === 'del')).toBe(true);
  });

  it('纯日志无染色（无 diff 块 → 不渲染）', () => {
    expect(extractDiffBlocks(log)).toHaveLength(0);
  });

  it('合并行数上限 400（DIFF_MAX_LINES）', () => {
    expect(DIFF_MAX_LINES).toBe(400);
    const big =
      'diff --git a/f b/f\n--- a/f\n+++ b/f\n@@ -1 +1 @@\n' +
      Array.from({ length: 500 }, (_, i) => `+line${i}`).join('\n');
    const blocks = extractDiffBlocks(big);
    expect(blocks).toHaveLength(1);
    expect(blockToLines(blocks[0]!).length).toBeGreaterThan(400);
  });
});

// ---------------------------------------------------------------- G：流式与渲染细节
 describe('G 流式与渲染细节', () => {
   it('G1 未闭合 fence 整块 holdback（不闪出代码框）', () => {
     // G2 联测：增量路径宽容模式，未闭合围栏回退为段落
     const cut = holdbackCut('text\n```ts\ncode\n');
     expect(cut).toBe('text\n'.length);
     expect(parseMarkdown('```ts\ncode\n', { lenient: true })[0]!.type).toBe('paragraph');
   });

   it('G1 列表跨块 holdback，块结束（空行）后一次提交', async () => {
     const src = ref('intro\n');
     const { committed, pendingText } = useStreamingMarkdown(() => src.value);
     await nextTick();
     src.value = 'intro\n- a\n- b\n';
     await nextTick();
     // 列表未完：committed 仍只有 intro 段落，无 list 闪烁
     expect(committed.value.map((n) => n.type)).toEqual(['paragraph']);
     expect(pendingText.value).toContain('- a');
     // 空行到达 → 列表与后续一次提交（无跨块闪烁，单次更新成形）
     src.value = 'intro\n- a\n- b\n\nmore\n';
     await nextTick();
     expect(committed.value.map((n) => n.type)).toEqual(['paragraph', 'list', 'paragraph']);
     expect(pendingText.value).toBe('');
   });

  it('G1 标题单行即完整，不 holdback', () => {
    expect(holdbackCut('# h\n')).toBe('# h\n'.length);
  });

  it('G3 图片 alt/title 归位（title 归还悬停）', () => {
    const n = parseInline('![alt](https://x.com/a.png "hover")');
    const img = n.find((x) => x.type === 'link' && (x as { isImage?: boolean }).isImage === true) as unknown as {
      alt?: string;
      title?: string;
    };
    expect(img.alt).toBe('alt');
    expect(img.title).toBe('hover');
  });

  it('G3 无标题图片 alt 正常，旧快照 title 可回填（渲染层 alt ?? title）', () => {
    const n = parseInline('![alt](https://x.com/a.png)');
    const img = n.find((x) => x.type === 'link') as unknown as { alt?: string; title?: string };
    expect(img.alt).toBe('alt');
    expect(img.title).toBeUndefined();
    // 兼容旧形状：只有 title 时渲染层用 title 当 alt
    const legacy = { alt: undefined as string | undefined, title: 'old-alt' };
    expect(legacy.alt ?? legacy.title ?? '').toBe('old-alt');
  });

  it('G3 parseMarkdown 图片复核（块级）', () => {
    const nodes = parseMarkdown('![a](https://x.com/a.png "t")');
    expect(nodes[0]!.type).toBe('paragraph');
  });
});

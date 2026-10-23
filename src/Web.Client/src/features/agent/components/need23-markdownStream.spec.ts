/** need23-R5：流式 Markdown 块渲染单测（增量追加 / settled 复用 / 超限降级）。 */
import { describe, expect, it } from 'vitest';
import { mount } from '@vue/test-utils';
import { defineComponent, h, ref } from 'vue';
import MarkdownStreamBlocks from '@features/agent/components/MarkdownStreamBlocks.vue';

/** 用动态 props 的宿主组件，便于模拟「逐次追加」的流式过程。 */
function makeHost(initial: string, cacheKey = 'k1') {
  const text = ref(initial);
  const Host = defineComponent({
    setup() {
      return () => h(MarkdownStreamBlocks, { text: text.value, cacheKey });
    },
  });
  const wrapper = mount(Host);
  return { wrapper, text };
}

describe('MarkdownStreamBlocks（need23-R5）', () => {
  it('标题 + 段落渲染为 markdown 结构（不再是纯文本）', async () => {
    const { wrapper } = makeHost('# 标题\n\n正文段落');
    await wrapper.vm.$nextTick();
    const html = wrapper.html();
    expect(html).toContain('<h1>');
    expect(html).toContain('<p>');
    expect(wrapper.find('.markdown-body').exists()).toBe(true);
  });

  it('逐次追加：块数单调不减，且已 settled 块 html 不再变化', async () => {
    const { wrapper, text } = makeHost('第一段\n\n');
    await wrapper.vm.$nextTick();
    const firstBlockCount = wrapper.findAll('.stream-block').length;

    text.value = '第一段\n\n第二段\n\n';
    await wrapper.vm.$nextTick();
    const secondBlocks = wrapper.findAll('.stream-block');
    expect(secondBlocks.length).toBeGreaterThanOrEqual(firstBlockCount);
    // 首块已 settled：内容保持
    expect(secondBlocks[0]?.html()).toContain('第一段');

    text.value = '第一段\n\n第二段\n\n```js\nconst a = 1;\n```\n';
    await wrapper.vm.$nextTick();
    const html = wrapper.html();
    expect(html).toContain('<pre');
    expect(secondBlocks[0]?.html()).toContain('第一段');
  });

  it('代码块渲染形态与历史区同源（language-x + pre/code）', async () => {
    const { wrapper } = makeHost('```js\nconst x = 1;\n```\n', 'k2');
    await wrapper.vm.$nextTick();
    const html = wrapper.html();
    expect(html).toContain('<pre>');
    expect(html).toContain('class="language-js"');
    expect(html).toContain('const x = 1;');
  });

  it('空文本不渲染任何块', async () => {
    const { wrapper } = makeHost('', 'k3');
    await wrapper.vm.$nextTick();
    expect(wrapper.findAll('.stream-block')).toHaveLength(0);
  });

  it('文本变短（重放/重置）不沿用旧块', async () => {
    const { wrapper, text } = makeHost('# 一\n\n段落A\n\n段落B', 'k4');
    await wrapper.vm.$nextTick();
    text.value = '# 二';
    await wrapper.vm.$nextTick();
    const html = wrapper.html();
    expect(html).toContain('二');
    expect(html).not.toContain('段落A');
  });

  it('整源超 128KB 降级纯文本并给提示', async () => {
    const big = 'x'.repeat(128 * 1024 + 1);
    const { wrapper } = makeHost(big, 'k5');
    await wrapper.vm.$nextTick();
    expect(wrapper.html()).toContain('<pre>');
    expect(wrapper.html()).toContain('内容超长');
  });

  it('不同 cacheKey 互不污染', async () => {
    const a = makeHost('# A', 'key-a');
    const b = makeHost('# B', 'key-b');
    await a.wrapper.vm.$nextTick();
    await b.wrapper.vm.$nextTick();
    expect(a.wrapper.html()).toContain('A');
    expect(b.wrapper.html()).toContain('B');
    expect(b.wrapper.html()).not.toContain('>A<');
  });
});

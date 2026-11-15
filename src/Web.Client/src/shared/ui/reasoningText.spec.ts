/** 思考块渲染壳单测：结构类名 / 空判 / 首尾裁剪 / 纯文本防注入。 */
import { describe, expect, it } from 'vitest';
import { mount } from '@vue/test-utils';
import ReasoningText from '@shared/ui/ReasoningText.vue';

function mountBlock(raw: string | null | undefined) {
  return mount(ReasoningText, { props: { raw } });
}

describe('ReasoningText 思考块渲染壳', () => {
  it('结构与类名：外壳微底 + 主题色左线，内层滚动槽位', () => {
    const wrapper = mountBlock('abc');
    expect(wrapper.find('[data-testid="reasoning-block"]').exists()).toBe(true);
    expect(wrapper.find('.reasoning-seg').exists()).toBe(true);
    const html = wrapper.html();
    expect(html).toContain('max-h-[40vh]');
    expect(html).toContain('border-accent');
    expect(html).toContain('bg-[var(--reasoning-block-bg)]');
  });

  it('空判：纯空白原文不输出空色块', () => {
    expect(mountBlock('\n\n  \n').find('[data-testid="reasoning-block"]').exists()).toBe(false);
    expect(mountBlock('\n').find('[data-testid="reasoning-block"]').exists()).toBe(false);
    expect(mountBlock('\n\n').find('[data-testid="reasoning-block"]').exists()).toBe(false);
    expect(mountBlock('').find('[data-testid="reasoning-block"]').exists()).toBe(false);
    expect(mountBlock(null).find('[data-testid="reasoning-block"]').exists()).toBe(false);
    expect(mountBlock(undefined).find('[data-testid="reasoning-block"]').exists()).toBe(false);
  });

  it('首尾裁剪：头空行清空、尾部折叠为 1 个换行', () => {
    const wrapper = mountBlock('\n\n  abc  \n\n');
    const seg = wrapper.find('.reasoning-seg');
    expect(seg.element.textContent).toBe('abc\n');
  });

  it('中间空行保留', () => {
    const wrapper = mountBlock('a\n\nb');
    expect(wrapper.find('.reasoning-seg').element.textContent).toBe('a\n\nb');
  });

  it('无尾换行不补', () => {
    const wrapper = mountBlock('abc');
    const seg = wrapper.find('.reasoning-seg');
    expect(seg.element.textContent).toBe('abc');
    expect(String(seg.element.textContent).endsWith('\n')).toBe(false);
  });

  it('不透传 Markdown：纯文本插值，无标签解析', () => {
    const wrapper = mountBlock('**x**<img src=x onerror=1>');
    const html = wrapper.html();
    expect(html).not.toContain('<strong>');
    expect(html).not.toContain('<img');
  });
});

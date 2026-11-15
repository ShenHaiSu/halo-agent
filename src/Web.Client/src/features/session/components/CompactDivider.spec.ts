import { describe, expect, it } from 'vitest';
import { mount } from '@vue/test-utils';
import CompactDivider from './CompactDivider.vue';

function mountDivider(over: Record<string, unknown> = {}) {
  return mount(CompactDivider, {
    props: {
      stubId: 'stub-1',
      label: '上下文压缩 · 42 条 → 摘要',
      summary: '摘要正文',
      summaryHtml: '<p>摘要正文</p>',
      backupFile: null,
      expanded: false,
      ...over,
    },
  });
}

describe('P007 CompactDivider 通栏桩', () => {
  it('C1：折叠默认摘要不可见且 pill 标记收起', () => {
    const wrapper = mountDivider();
    expect(wrapper.find('[data-testid="compact-summary"]').exists()).toBe(false);
    expect(wrapper.find('[data-testid="compact-pill"]').attributes('aria-expanded')).toBe('false');
  });

  it('C2：点击 pill 透出 toggle，展开态渲染摘要 HTML', async () => {
    const wrapper = mountDivider();
    await wrapper.find('[data-testid="compact-pill"]').trigger('click');
    expect(wrapper.emitted('toggle')).toHaveLength(1);
    expect(wrapper.find('[data-testid="compact-pill"]').attributes('aria-expanded')).toBe('false');
    const open = mountDivider({ expanded: true });
    expect(open.find('[data-testid="compact-pill"]').attributes('aria-expanded')).toBe('true');
    expect(open.find('[data-testid="compact-summary"]').exists()).toBe(true);
    expect(open.find('[data-testid="compact-summary"]').html()).toContain('<p>摘要正文</p>');
  });

  it('C3：根节点通栏且不受气泡限宽影响', () => {
    const wrapper = mountDivider();
    const cls = String(wrapper.find('[data-testid="compact-divider"]').attributes('class') || '');
    expect(cls.split(/\s+/)).toContain('w-full');
    expect(wrapper.html()).not.toContain('max-w-[85%]');
  });

  it('C4：备份提示按需显示', () => {
    expect(mountDivider().find('[data-testid="compact-summary"]').exists()).toBe(false);
    const open = mountDivider({ expanded: true, backupFile: 'messages.compact-bak-x.jsonl' });
    expect(open.find('[data-testid="compact-summary"]').text()).toContain('messages.compact-bak-x.jsonl');
    const noBak = mountDivider({ expanded: true, backupFile: null });
    expect(noBak.find('[data-testid="compact-summary"]').text()).not.toContain('备份');
  });

  it('C5：无摘要时不渲染空块但 pill 可点', async () => {
    const wrapper = mountDivider({ expanded: true, summary: '', summaryHtml: '' });
    expect(wrapper.find('[data-testid="compact-summary"]').exists()).toBe(false);
    await wrapper.find('[data-testid="compact-pill"]').trigger('click');
    expect(wrapper.emitted('toggle')).toHaveLength(1);
  });
});

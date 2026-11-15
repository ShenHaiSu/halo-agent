/** 回到底部圆钮：36px 主题色圆 + SVG 单箭头（TimelineView show-jump）。 */
import { describe, expect, it } from 'vitest';
import { mount } from '@vue/test-utils';
import naive from 'naive-ui';
import TimelineView from '@features/agent/components/TimelineView.vue';

function mountTimeline(opts?: { showJumpBtn?: boolean; readingWindow?: boolean }) {
  return mount(TimelineView, {
    props: {
      isEmpty: false,
      items: [],
      chatWidthMode: 'centered',
      showJumpBtn: opts?.showJumpBtn ?? false,
      readingWindow: opts?.readingWindow ?? false,
    },
    global: { plugins: [naive] },
  });
}

describe('回到底部圆钮（show-jump）', () => {
  it('默认隐藏：showJumpBtn=false 且 readingWindow=false 时不渲染', () => {
    const wrapper = mountTimeline();
    expect(wrapper.find('[data-testid="show-jump"]').exists()).toBe(false);
  });

  it('上翻亮出：showJumpBtn=true 时渲染 36px 圆钮 + SVG 单箭头', () => {
    const wrapper = mountTimeline({ showJumpBtn: true });
    const btn = wrapper.find('[data-testid="show-jump"]');
    expect(btn.exists()).toBe(true);
    expect(btn.attributes('title')).toBe('回到底部');
    expect(btn.attributes('aria-label')).toBe('回到底部');
    // 文字已替换为图标：不再含文本，含 svg
    expect(btn.text()).not.toContain('回到底部');
    expect(btn.find('svg').exists()).toBe(true);
  });

  it('阅读窗也亮：readingWindow=true 时同样渲染', () => {
    const wrapper = mountTimeline({ readingWindow: true });
    expect(wrapper.find('[data-testid="show-jump"]').exists()).toBe(true);
  });

  it('点击回到底部：emit jump-latest', async () => {
    const wrapper = mountTimeline({ showJumpBtn: true });
    await wrapper.find('[data-testid="show-jump"]').trigger('click');
    expect(wrapper.emitted('jump-latest')).toBeTruthy();
  });
});

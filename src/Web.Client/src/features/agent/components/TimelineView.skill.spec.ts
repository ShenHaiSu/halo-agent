/** 历史气泡分段渲染单测。 */
import { describe, expect, it } from 'vitest';
import { mount } from '@vue/test-utils';
import naive from 'naive-ui';
import TimelineView from '@features/agent/components/TimelineView.vue';

function mountTimeline(items: never[]) {
  return mount(TimelineView, {
    props: {
      isEmpty: false,
      items,
      chatWidthMode: 'centered',
    },
    global: { plugins: [naive] },
  });
}

describe('TimelineView user 气泡（H4）', () => {
  it('H4 含 marker 的 user 消息渲染成 chip（而非裸 marker 文本）', () => {
    const items = [
      {
        key: 'k1',
        kind: 'user',
        msg: { id: 'm1', role: 'user', content: '帮我改代码 [[skill:global/foo]] 谢谢', createdAtUtc: '' },
      },
    ] as never[];
    const wrapper = mountTimeline(items);
    expect(wrapper.find('[data-testid="skill-chip-global-foo"]').exists()).toBe(true);
    expect(wrapper.text()).not.toContain('[[skill:');
  });
});

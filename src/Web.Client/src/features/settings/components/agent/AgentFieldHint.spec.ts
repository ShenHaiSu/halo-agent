import { describe, expect, it } from 'vitest';
import { mount } from '@vue/test-utils';
import naive from 'naive-ui';
import AgentFieldHint from './AgentFieldHint.vue';

describe('AgentFieldHint（说明收敛后的 tooltip）', () => {
  const text = '开启后写入 0（不限时），关闭恢复默认 10 分钟 / 600000ms。';

  function mountHint() {
    return mount(AgentFieldHint, {
      props: { text, label: '查看无限时间说明' },
      global: { plugins: [naive] },
    });
  }

  it('说明不再常驻：默认收起，图标可聚焦且带可读名称', () => {
    const w = mountHint();
    const trigger = w.get('.agent-field-info');
    expect(w.findComponent({ name: 'Tooltip' }).props('show')).toBe(false);
    expect(trigger.attributes('tabindex')).toBe('0');
    expect(trigger.attributes('role')).toBe('button');
    expect(trigger.attributes('aria-label')).toBe('查看无限时间说明');
  });

  it('鼠标悬停与移出控制显隐', async () => {
    const w = mountHint();
    const tip = () => w.findComponent({ name: 'Tooltip' });
    await w.get('.agent-field-info').trigger('mouseenter');
    expect(tip().props('show')).toBe(true);
    await w.get('.agent-field-info').trigger('mouseleave');
    expect(tip().props('show')).toBe(false);
  });

  it('键盘焦点与失焦同样控制显隐', async () => {
    const w = mountHint();
    const tip = () => w.findComponent({ name: 'Tooltip' });
    await w.get('.agent-field-info').trigger('focus');
    expect(tip().props('show')).toBe(true);
    await w.get('.agent-field-info').trigger('blur');
    expect(tip().props('show')).toBe(false);
  });
});

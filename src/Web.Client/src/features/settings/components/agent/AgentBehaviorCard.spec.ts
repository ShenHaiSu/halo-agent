import { afterEach, describe, expect, it } from 'vitest';
import { enableAutoUnmount, mount } from '@vue/test-utils';
import naive from 'naive-ui';
import { defineComponent, h, nextTick, ref } from 'vue';
import AgentBehaviorCard from './AgentBehaviorCard.vue';

enableAutoUnmount(afterEach);

type CardProps = {
  title?: string;
  summary?: string;
  open?: boolean;
  dirty?: boolean;
  disabled?: boolean;
  testId?: string;
};

function mountCard(overrides: CardProps = {}) {
  return mount(AgentBehaviorCard, {
    attachTo: document.body,
    props: {
      title: '命令执行超时',
      summary: '默认 30 秒 · 上限 5 分钟',
      open: true,
      testId: 'agent-card-timeout',
      ...overrides,
    },
    slots: {
      default: () => h('button', { type: 'button', 'data-testid': 'agent-card-body-action' }, '正文操作'),
      'header-extra': () => h('button', { type: 'button', 'data-testid': 'agent-card-header-action' }, '恢复默认'),
    },
    global: { plugins: [naive], stubs: { transition: false } },
  });
}

describe('AgentBehaviorCard', () => {
  it('默认展开并关联正文', () => {
    const wrapper = mountCard();
    const toggle = wrapper.get('[data-testid="agent-card-toggle-timeout"]');
    const body = wrapper.get(`#${toggle.attributes('aria-controls')}`);

    expect(toggle.attributes('aria-expanded')).toBe('true');
    expect(toggle.attributes('aria-controls')).toBe('agent-card-timeout-body');
    expect(body.isVisible()).toBe(true);
    expect(body.attributes('aria-hidden')).toBe('false');
    expect(body.attributes('inert')).toBeUndefined();
  });

  it('点击 toggle 发出折叠事件', async () => {
    const wrapper = mountCard();

    await wrapper.get('[data-testid="agent-card-toggle-timeout"]').trigger('click');

    expect(wrapper.emitted('update:open')).toEqual([[false]]);
  });

  it('折叠后正文不可见、不可交互且对辅助技术隐藏', async () => {
    const wrapper = mountCard();
    const controls = wrapper.get('.agent-card-toggle').attributes('aria-controls');

    await wrapper.setProps({ open: false });
    await nextTick();

    const body = wrapper.get(`#${controls}`);
    expect(body.attributes('aria-hidden')).toBe('true');
    expect(body.attributes()).toHaveProperty('inert');
    expect(body.isVisible()).toBe(false);
    expect(wrapper.get('.agent-card-toggle').attributes('aria-expanded')).toBe('false');
  });

  it('点击 header-extra 不触发卡片折叠', async () => {
    const wrapper = mountCard();

    await wrapper.get('[data-testid="agent-card-header-action"]').trigger('click');

    expect(wrapper.emitted('update:open')).toBeUndefined();
    expect(wrapper.get('.agent-card-toggle').attributes('aria-expanded')).toBe('true');
  });

  it('脏态显示主题色标记及可访问说明', () => {
    const wrapper = mountCard({ dirty: true });
    const dirty = wrapper.get('[data-testid="agent-card-dirty-timeout"]');

    expect(dirty.classes()).toContain('agent-card-dirty');
    expect(dirty.attributes('title')).toBe('有未保存更改');
    expect(dirty.attributes('aria-label')).toBe('有未保存更改');
    expect(wrapper.get('.agent-card-toggle').attributes('aria-label')).toContain('有未保存更改');
    expect(mountCard({ dirty: false }).find('.agent-card-dirty').exists()).toBe(false);
  });

  it('摘要正确传入并可省略', () => {
    const wrapper = mountCard({ summary: '32 轮 · 单轮 10 分钟' });

    expect(wrapper.get('.agent-card-summary').text()).toBe('32 轮 · 单轮 10 分钟');
    expect(mountCard({ summary: undefined }).get('.agent-card-summary').text()).toBe('');
  });

  it('正文获得焦点时折叠会将焦点移回 toggle', async () => {
    const wrapper = mountCard();
    const toggle = wrapper.get<HTMLButtonElement>('.agent-card-toggle');
    const bodyButton = wrapper.get<HTMLButtonElement>('[data-testid="agent-card-body-action"]');

    bodyButton.element.focus();
    expect(document.activeElement).toBe(bodyButton.element);

    await wrapper.setProps({ open: false });
    await nextTick();

    expect(document.activeElement).toBe(toggle.element);
  });

  it('折叠时不会抢走正文外的焦点', async () => {
    const wrapper = mountCard();
    const outside = document.createElement('button');
    document.body.appendChild(outside);
    outside.focus();

    await wrapper.setProps({ open: false });
    await nextTick();

    expect(document.activeElement).toBe(outside);
    outside.remove();
  });

  it('禁用状态禁止 toggle 并标记卡片', async () => {
    const wrapper = mountCard({ disabled: true });
    const toggle = wrapper.get('.agent-card-toggle');

    expect(toggle.attributes('disabled')).toBeDefined();
    expect(wrapper.get('.agent-behavior-card').classes()).toContain('is-disabled');
    await toggle.trigger('click');
    expect(wrapper.emitted('update:open')).toBeUndefined();
  });

  it('缺少 testId 时正文 id 在同一应用内稳定且唯一', async () => {
    const firstTitle = ref('运行监控');
    const Pair = defineComponent({
      setup() {
        return () =>
          h('div', [
            h(AgentBehaviorCard, { title: firstTitle.value, open: true }),
            h(AgentBehaviorCard, { title: '拒绝记录', open: true }),
          ]);
      },
    });
    const wrapper = mount(Pair, { global: { plugins: [naive], stubs: { transition: false } } });
    const controls = wrapper.findAll('.agent-card-toggle').map((toggle) => toggle.attributes('aria-controls'));

    expect(controls[0]).toMatch(/^agent-card-.+-body$/);
    expect(controls[1]).toMatch(/^agent-card-.+-body$/);
    expect(new Set(controls).size).toBe(2);

    firstTitle.value = '运行监控（已更新）';
    await nextTick();
    expect(wrapper.findAll('.agent-card-toggle')[0].attributes('aria-controls')).toBe(controls[0]);
  });
});

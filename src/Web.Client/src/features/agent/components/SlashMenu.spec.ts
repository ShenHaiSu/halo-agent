/** need22-F03：SlashMenu 组件单测（frontend/03 §10 S14/S15，@vue/test-utils）。 */
import { describe, expect, it } from 'vitest';
import { mount } from '@vue/test-utils';
import SlashMenu from '@features/agent/components/SlashMenu.vue';
import type { SlashCandidate } from '@shared/utils/slashRegistry';

const CANDIDATES: SlashCandidate[] = [
  { id: 'skill:global:alpha', providerId: 'skill', scope: 'global', prefix: 'global/', label: 'global/alpha', title: 'Alpha 技能' },
  { id: 'skill:project:beta', providerId: 'skill', scope: 'project', prefix: 'project/', label: 'project/beta' },
];

describe('SlashMenu.vue（S14/S15）', () => {
  it('S14 渲染候选、hover/click 发事件', async () => {
    const wrapper = mount(SlashMenu, {
      props: { visible: true, candidates: CANDIDATES, index: 0, error: '', loading: false },
    });
    expect(wrapper.find('[data-testid="slash-menu"]').exists()).toBe(true);
    const items = wrapper.findAll('[data-testid^="slash-item-"]');
    expect(items.length).toBe(2);
    expect(wrapper.find('[data-testid="slash-item-skill:global:alpha"]').exists()).toBe(true);
    // 无障碍：listbox / option / aria-selected
    expect(wrapper.find('[data-testid="slash-menu"]').attributes('role')).toBe('listbox');
    expect(items[0].attributes('role')).toBe('option');
    expect(items[0].attributes('aria-selected')).toBe('true');
    expect(items[1].attributes('aria-selected')).toBe('false');
    // hover
    await items[1].trigger('mouseenter');
    expect(wrapper.emitted('hover')).toBeTruthy();
    expect(wrapper.emitted('hover')?.[0]).toEqual([1]);
    // click
    await items[0].trigger('click');
    expect(wrapper.emitted('select')).toBeTruthy();
    expect(wrapper.emitted('select')?.[0]?.[0]).toEqual(CANDIDATES[0]);
  });

  it('S15 空结果显示"无匹配项"', () => {
    const wrapper = mount(SlashMenu, {
      props: { visible: true, candidates: [], index: 0, error: '', loading: false },
    });
    expect(wrapper.find('[data-testid="slash-menu-empty"]').text()).toContain('无匹配项');
  });

  it('visible=false 时不渲染面板', () => {
    const wrapper = mount(SlashMenu, {
      props: { visible: false, candidates: CANDIDATES, index: 0, error: '', loading: false },
    });
    expect(wrapper.find('[data-testid="slash-menu"]').exists()).toBe(false);
  });

  it('error 文案透传到面板顶部', () => {
    const wrapper = mount(SlashMenu, {
      props: { visible: true, candidates: [], index: 0, error: '技能通道不可用', loading: false },
    });
    expect(wrapper.find('[data-testid="slash-menu-error"]').text()).toBe('技能通道不可用');
  });
});

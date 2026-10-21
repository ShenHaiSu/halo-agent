/** need22-F04：展示态收口单测（frontend/04 §7 H1~H3 / H6）。 */
import { describe, expect, it } from 'vitest';
import { mount } from '@vue/test-utils';
import naive from 'naive-ui';
import { userParts } from '@features/agent/utils/userParts';
import SkillPillChip from '@features/agent/components/SkillPillChip.vue';

describe('userParts（H1~H3）', () => {
  it('H1 userParts("a[[skill:global/foo]]b") ⇒ 3 段：text / skill / text', () => {
    const parts = userParts('a[[skill:global/foo]]b');
    expect(parts.length).toBe(3);
    expect(parts[0]).toEqual({ type: 'text', text: 'a' });
    expect(parts[1]).toMatchObject({ type: 'skill', scope: 'global', name: 'foo' });
    expect(parts[2]).toEqual({ type: 'text', text: 'b' });
  });

  it('H2 userParts("纯文本") ⇒ 1 段 text', () => {
    const parts = userParts('纯文本');
    expect(parts.length).toBe(1);
    expect(parts[0]).toEqual({ type: 'text', text: '纯文本' });
  });

  it('H3 userParts("") ⇒ 1 段 text（空串）', () => {
    const parts = userParts('');
    expect(parts.length).toBe(1);
    expect(parts[0]).toEqual({ type: 'text', text: '' });
  });
});

describe('SkillPillChip（H6）', () => {
  it('H6 渲染 scope 徽标 + 名称', () => {
    const wrapper = mount(SkillPillChip, {
      props: { scope: 'global', name: 'foo' },
      global: { plugins: [naive] },
    });
    const chip = wrapper.find('[data-testid="skill-chip-global-foo"]');
    expect(chip.exists()).toBe(true);
    expect(chip.find('.skill-chip-scope').text()).toBe('global');
    expect(chip.find('.skill-chip-name').text()).toBe('foo');
    expect(chip.attributes('aria-label')).toBe('技能 global/foo');
  });
});

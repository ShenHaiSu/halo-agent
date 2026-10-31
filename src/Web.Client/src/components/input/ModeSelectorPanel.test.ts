// src/Web.Client/src/components/input/ModeSelectorPanel.test.ts
import { describe, expect, it } from 'vitest';
import { mount } from '@vue/test-utils';
import ModeSelectorPanel from './ModeSelectorPanel.vue';
import { PERMISSION_MODES } from './permissionModes';

describe('ModeSelectorPanel', () => {
  it('渲染 4 档权限模式', () => {
    const w = mount(ModeSelectorPanel, { props: { current: 'ask' } });
    const options = w.findAll('[role="option"]');
    expect(options.length).toBe(PERMISSION_MODES.length);
    for (const m of PERMISSION_MODES) {
      expect(w.text()).toContain(m.label);
      expect(w.text()).toContain(m.desc);
    }
    w.unmount();
  });

  it('当前档高亮（aria-selected + current）', () => {
    const w = mount(ModeSelectorPanel, { props: { current: 'yolo' } });
    const options = w.findAll('[role="option"]');
    const cur = options.find((o) => o.attributes('aria-selected') === 'true');
    expect(cur).toBeTruthy();
    expect(cur!.classes()).toContain('current');
    expect(cur!.text()).toContain('完全放行');
    w.unmount();
  });

  it('点击选项 emit select（无二次确认，直接带值）', async () => {
    const w = mount(ModeSelectorPanel, { props: { current: 'ask' } });
    const options = w.findAll('[role="option"]');
    await options[2]!.trigger('click');
    expect(w.emitted('select')).toBeTruthy();
    expect(w.emitted('select')![0]).toEqual(['bypassPermissions']);
    w.unmount();
  });

  it('Enter 选中高亮行并 emit select', async () => {
    const w = mount(ModeSelectorPanel, { props: { current: 'ask' } });
    await w.find('[role="listbox"]').trigger('keydown', { key: 'End' });
    await w.find('[role="listbox"]').trigger('keydown', { key: 'Enter' });
    expect(w.emitted('select')![0]).toEqual(['yolo']);
    w.unmount();
  });

  it('页脚可去权限设置', async () => {
    const w = mount(ModeSelectorPanel, { props: { current: 'ask' } });
    await w.find('.pm-foot button').trigger('click');
    expect(w.emitted('openSettings')).toBeTruthy();
    w.unmount();
  });
});

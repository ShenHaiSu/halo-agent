// src/Web.Client/src/components/base/HDialog.test.ts
import { describe, expect, it } from 'vitest';
import { mount } from '@vue/test-utils';
import { nextTick } from 'vue';
import HDialog from './HDialog.vue';

describe('HDialog', () => {
  it('open=false 时不渲染面板', () => {
    const w = mount(HDialog, {
      props: { open: false, title: 'T' },
      slots: { default: '内容' },
      attachTo: document.body,
    });
    expect(document.querySelector('[role="dialog"]')).toBeNull();
    w.unmount();
  });

  it('open=true 时渲染面板且有 aria-modal', async () => {
    const w = mount(HDialog, {
      props: { open: false, title: '标题' },
      slots: { default: '内容' },
      attachTo: document.body,
    });
    await w.setProps({ open: true });
    await nextTick();
    const panel = document.querySelector('[role="dialog"]');
    expect(panel).not.toBeNull();
    expect(panel!.getAttribute('aria-modal')).toBe('true');
    expect(panel!.getAttribute('aria-label')).toBe('标题');
    w.unmount();
  });

  it('按 ESC 关闭（非 persistent）', async () => {
    const w = mount(HDialog, {
      props: { open: false },
      slots: { default: 'x' },
      attachTo: document.body,
    });
    await w.setProps({ open: true });
    await nextTick();
    const wrapper = document.querySelector('[role="presentation"]') as HTMLElement;
    wrapper.dispatchEvent(new KeyboardEvent('keydown', { key: 'Escape', bubbles: true }));
    await nextTick();
    expect(w.emitted('update:open')?.[0]).toEqual([false]);
    w.unmount();
  });

  it('persistent 时 ESC 不关闭', async () => {
    const w = mount(HDialog, {
      props: { open: false, persistent: true },
      slots: { default: 'x' },
      attachTo: document.body,
    });
    await w.setProps({ open: true });
    await nextTick();
    const wrapper = document.querySelector('[role="presentation"]') as HTMLElement;
    wrapper.dispatchEvent(new KeyboardEvent('keydown', { key: 'Escape', bubbles: true }));
    await nextTick();
    expect(w.emitted('update:open')).toBeUndefined();
    w.unmount();
  });

  it('打开时锁定 body 滚动', async () => {
    const w = mount(HDialog, { props: { open: false }, attachTo: document.body });
    await w.setProps({ open: true });
    await nextTick();
    expect(document.body.style.overflow).toBe('hidden');
    await w.setProps({ open: false });
    await nextTick();
    expect(document.body.style.overflow).toBe('');
    w.unmount();
  });

  it('关闭时把焦点还给触发元素', async () => {
    const btn = document.createElement('button');
    document.body.appendChild(btn);
    btn.focus();

    const w = mount(HDialog, { props: { open: false }, attachTo: document.body });
    await w.setProps({ open: true });
    await nextTick();
    // 焦点此时在面板内
    expect(document.activeElement === btn).toBe(false);

    await w.setProps({ open: false });
    await nextTick();
    expect(document.activeElement).toBe(btn);

    w.unmount();
    btn.remove();
  });

  it('点击遮罩关闭（非 persistent）', async () => {
    const w = mount(HDialog, {
      props: { open: false },
      slots: { default: 'x' },
      attachTo: document.body,
    });
    await w.setProps({ open: true });
    await nextTick();
    const backdrop = document.querySelector('[aria-hidden="true"]') as HTMLElement;
    backdrop.click();
    await nextTick();
    expect(w.emitted('update:open')?.[0]).toEqual([false]);
    w.unmount();
  });
});

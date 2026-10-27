// src/Web.Client/src/components/base/HButton.test.ts
import { describe, expect, it } from 'vitest';
import { mount } from '@vue/test-utils';
import HButton from './HButton.vue';

describe('HButton', () => {
  it('渲染 slot 内容', () => {
    const w = mount(HButton, { slots: { default: '点我' } });
    expect(w.text()).toContain('点我');
  });

  it('默认 type 是 button（避免在 form 里意外提交）', () => {
    const w = mount(HButton, { slots: { default: 'x' } });
    expect(w.attributes('type')).toBe('button');
  });

  it('点击时 emit click', async () => {
    const w = mount(HButton, { slots: { default: 'x' } });
    await w.trigger('click');
    expect(w.emitted('click')).toHaveLength(1);
  });

  it('disabled 时不 emit click', async () => {
    const w = mount(HButton, { props: { disabled: true }, slots: { default: 'x' } });
    await w.trigger('click');
    expect(w.emitted('click')).toBeUndefined();
    expect(w.attributes('disabled')).toBeDefined();
  });

  it('loading 时 disabled 且 aria-busy', () => {
    const w = mount(HButton, { props: { loading: true }, slots: { default: 'x' } });
    expect(w.attributes('disabled')).toBeDefined();
    expect(w.attributes('aria-busy')).toBe('true');
  });

  it('loading 时文字仍在 DOM 里（占位，避免宽度抖动）', () => {
    const w = mount(HButton, { props: { loading: true }, slots: { default: '保存' } });
    expect(w.text()).toContain('保存');
    // 文字外层应当 opacity-0
    const span = w.find('span.opacity-0');
    expect(span.exists()).toBe(true);
  });

  it('loading 时点击不 emit', async () => {
    const w = mount(HButton, { props: { loading: true }, slots: { default: 'x' } });
    await w.trigger('click');
    expect(w.emitted('click')).toBeUndefined();
  });

  it('block 时加 w-full', () => {
    const w = mount(HButton, { props: { block: true }, slots: { default: 'x' } });
    expect(w.classes()).toContain('w-full');
  });

  it('三种 size 的高度类不同', () => {
    const sm = mount(HButton, { props: { size: 'sm' }, slots: { default: 'x' } });
    const lg = mount(HButton, { props: { size: 'lg' }, slots: { default: 'x' } });
    expect(sm.classes()).toContain('h-7');
    expect(lg.classes()).toContain('h-10');
  });

  it('透传原生属性（title / data-*）', () => {
    const w = mount(HButton, {
      props: { title: '提示', 'data-test': 'btn' } as never,
      slots: { default: 'x' },
    });
    expect(w.attributes('title')).toBe('提示');
    expect(w.attributes('data-test')).toBe('btn');
  });

  it('icon 存在时渲染图标（非 loading）', () => {
    const w = mount(HButton, { props: { icon: 'search' }, slots: { default: 'x' } });
    expect(w.find('svg').exists()).toBe(true);
  });

  it('loading 时不渲染图标（由 spinner 代替）', () => {
    const w = mount(HButton, {
      props: { icon: 'search', loading: true },
      slots: { default: 'x' },
    });
    // spinner 也是 svg，所以统计 svg 数量：应该只有 1 个（spinner）
    expect(w.findAll('svg')).toHaveLength(1);
  });
});

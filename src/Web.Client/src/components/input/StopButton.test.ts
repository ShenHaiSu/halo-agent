import { describe, expect, it } from 'vitest';
import { mount } from '@vue/test-utils';
import StopButton from './StopButton.vue';

describe('StopButton 取消中态', () => {
  it('F7: 默认显示「停止」', () => {
    const w = mount(StopButton);
    expect(w.text()).toContain('停止');
    expect(w.find('button').attributes('aria-label')).toBe('停止生成');
  });

  it('F7: cancelling=true 显示「中断中…」+ loading + aria-label 变化', () => {
    const w = mount(StopButton, { props: { cancelling: true } });
    expect(w.text()).toContain('中断中…');
    expect(w.find('button').attributes('aria-label')).toBe('正在中断生成');
    // HButton loading → disabled（天然防连点）
    expect(w.find('button').attributes('disabled')).not.toBeUndefined();
  });
});

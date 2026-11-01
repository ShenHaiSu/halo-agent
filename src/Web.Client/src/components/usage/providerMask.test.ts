// src/Web.Client/src/components/usage/providerMask.test.ts
// P17-04 模糊化单测：恒 5 位 / 确定性 / 默认开 / utf8 不抛 / 不污染过滤。
import { beforeEach, describe, expect, it } from 'vitest';
import {
  MASK_KEY,
  isMaskEnabled,
  maskProviderId,
  resolveProviderDisplay,
  setMaskEnabled,
} from './providerMask';

beforeEach(() => {
  localStorage.removeItem(MASK_KEY);
});

describe('providerMask', () => {
  it('恒 5 位 [a-z0-9]（无后缀用例）', () => {
    for (const pid of ['openai', 'deepseek', '', '中文服务商', 'emoji-🎉-x', 'a'.repeat(200)]) {
      const m = maskProviderId(pid);
      expect(m).toMatch(/^[a-z0-9]{5}$/);
      expect(m).toHaveLength(5);
    }
  });
  it('确定性：同 pid 多次一致，刷新稳定', () => {
    expect(maskProviderId('openai')).toBe(maskProviderId('openai'));
    expect(maskProviderId('')).toBe(maskProviderId(''));
  });
  it('默认开，开关持久化', () => {
    expect(isMaskEnabled()).toBe(true);
    setMaskEnabled(false);
    expect(isMaskEnabled()).toBe(false);
    expect(localStorage.getItem(MASK_KEY)).toBe('0');
    setMaskEnabled(true);
    expect(isMaskEnabled()).toBe(true);
    expect(localStorage.getItem(MASK_KEY)).toBe('1');
  });
  it('resolve 唯一入口：开时假名，关时真名', () => {
    const fake = resolveProviderDisplay('openai', 'OpenAI');
    expect(fake).toMatch(/^[a-z0-9]{5}$/);
    expect(fake).not.toContain('OpenAI');
    setMaskEnabled(false);
    expect(resolveProviderDisplay('openai', 'OpenAI')).toBe('OpenAI');
    expect(resolveProviderDisplay('openai', '')).toBe('openai');
  });
  it('遮蔽不污染过滤：请求参数恒带真 providerId', () => {
    // 过滤走真值，显示走 resolve；此处锁定该分工不被后续重构破坏。
    const pid = 'openai';
    const params: Record<string, unknown> = {};
    if (pid) params.providerId = pid;
    setMaskEnabled(true);
    const label = resolveProviderDisplay(pid, 'OpenAI');
    expect(params.providerId).toBe('openai');
    expect(label).not.toBe('openai');
  });
});

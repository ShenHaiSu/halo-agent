// src/Web.Client/src/components/settings/modelProviders.test.ts
// 服务商切换必现崩溃的回归锁：真实契约形状（单数 model）一切换不得抛。
import { describe, expect, it } from 'vitest';
import {
  defaultModelOf,
  normalizeProviders,
  resolveProviderSwitch,
} from './modelProviders';

const BACKEND_SHAPE = [
  { name: 'ollama', model: 'qwen3:8b', baseUrl: 'http://localhost:11434/v1' },
  { name: 'deepseek', model: 'deepseek-chat', baseUrl: 'https://api.deepseek.com/v1' },
  { name: 'openrouter', model: 'qwen/qwen3-8b', baseUrl: 'https://openrouter.ai/api/v1' },
];

describe('normalizeProviders', () => {
  it('后端真实形状（单数 model）原样归一化', () => {
    const ps = normalizeProviders(BACKEND_SHAPE);
    expect(ps).toHaveLength(3);
    expect(ps[1]).toMatchObject({ name: 'deepseek', model: 'deepseek-chat' });
  });
  it('models 缺失/非数组永不抛', () => {
    expect(() => normalizeProviders(BACKEND_SHAPE)).not.toThrow();
    expect(normalizeProviders(null)).toEqual([]);
    expect(normalizeProviders(undefined)).toEqual([]);
    expect(normalizeProviders('x')).toEqual([]);
    expect(normalizeProviders([null, 42, { name: '' }, { name: 'a' }])).toEqual([
      { name: 'a', baseUrl: '', model: undefined, models: undefined },
    ]);
  });
  it('旧 models 数组形状仍兼容且优先', () => {
    const ps = normalizeProviders([{ name: 'x', baseUrl: 'b', model: 'm0', models: ['m1', 'm2'] }]);
    expect(defaultModelOf(ps[0])).toBe('m1');
  });
});

describe('resolveProviderSwitch（跟随但尊重手填）', () => {
  const ps = normalizeProviders(BACKEND_SHAPE);
   it('空 model 切 deepseek：baseUrl+model 双跟随，不抛', () => {
     expect(() => resolveProviderSwitch(ps, 'ollama', 'deepseek', '')).not.toThrow();
     const r = resolveProviderSwitch(ps, 'ollama', 'deepseek', '');
     expect(r).toMatchObject({
       baseUrl: 'https://api.deepseek.com/v1',
       model: 'deepseek-chat',
       followed: true,
     });
   });
   it('model 等于旧默认：视为没手改过，跟随新默认', () => {
     const r = resolveProviderSwitch(ps, 'ollama', 'openrouter', 'qwen3:8b');
     expect(r).toMatchObject({ model: 'qwen/qwen3-8b', followed: true });
   });
   it('用户手填（与旧默认不同）：保留手填，只换 baseUrl', () => {
     const r = resolveProviderSwitch(ps, 'ollama', 'deepseek', 'my-custom');
     expect(r).toMatchObject({ baseUrl: 'https://api.deepseek.com/v1', model: 'my-custom', followed: false });
   });
  it('未知服务商：返回 null（调用方仅标 dirty，不炸）', () => {
    expect(resolveProviderSwitch(ps, 'ollama', 'nope', '')).toBeNull();
  });
  it('新服务商无默认值：保持原值', () => {
    const ps2 = normalizeProviders([{ name: 'e', baseUrl: 'b' }]);
    expect(resolveProviderSwitch(ps2, 'e', 'e', 'keep')).toMatchObject({ model: 'keep' });
    expect(resolveProviderSwitch(ps2, 'x', 'e', '')).toMatchObject({ baseUrl: 'b', model: '' });
  });
});

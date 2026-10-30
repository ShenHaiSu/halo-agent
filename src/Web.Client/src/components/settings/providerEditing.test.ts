// src/Web.Client/src/components/settings/providerEditing.test.ts
// P10 providerEditing 纯函数单测：归一化/校验/dirty；左栏计数、空 provider、active 高亮、responses disabled、dirty 禁切 setActive。
import { describe, expect, it } from 'vitest';
import { diffActive, normalizeProvidersTree, validateProvidersTree } from './providerEditing';

const RAW = {
  activeProvider: 'deepseek',
  activeModel: 'deepseek-chat',
  providers: [
    {
      id: 'deepseek',
      displayName: 'DeepSeek',
      baseUrl: 'https://api.deepseek.com/v1',
      apiKeySet: true,
      headersEnabled: true,
      headers: [],
      useProxy: true,
      models: [
        { id: 'deepseek-chat', displayName: '', protocol: 'chat', contextWindow: 65536, maxTokens: null, acceptsImages: false, includeReasoningHistory: true },
      ],
    },
    { id: 'empty', displayName: '', baseUrl: 'https://e.local/v1', apiKeySet: false, headersEnabled: true, headers: [], useProxy: true, models: [] },
  ],
};

describe('normalizeProvidersTree', () => {
  it('整树归一化', () => {
    const t = normalizeProvidersTree(RAW);
    expect(t.providers).toHaveLength(2);
    expect(t.providers[0]!.models).toHaveLength(1);
    expect(t.providers[1]!.models).toHaveLength(0);
  });
  it('永不抛', () => {
    expect(() => normalizeProvidersTree(null)).not.toThrow();
    expect(normalizeProvidersTree(undefined).providers).toEqual([]);
  });
});

describe('validateProvidersTree', () => {
  it('合法无错', () => {
    expect(validateProvidersTree(normalizeProvidersTree(RAW))).toEqual([]);
  });
  it('空 provider 允许保存但 active 不可指向', () => {
    const t = normalizeProvidersTree({ activeProvider: 'empty', activeModel: 'x', providers: RAW.providers });
    expect(validateProvidersTree(t).some((e) => e.field === 'active')).toBe(true);
  });
  it('responses 协议放行（chat/responses 均可保存）', () => {
    const t = normalizeProvidersTree({
      activeProvider: 'deepseek',
      activeModel: 'm',
      providers: [{ id: 'deepseek', baseUrl: 'https://api.deepseek.com/v1', models: [{ id: 'm', protocol: 'responses' }] }],
    });
    expect(t.providers[0]!.models[0]!.protocol).toBe('responses');
    expect(validateProvidersTree(t).filter((e) => e.field === 'protocol')).toEqual([]);
  });
  it('未知协议仍拒绝保存', () => {
    const bad = normalizeProvidersTree({
      activeProvider: 'deepseek',
      activeModel: 'm',
      providers: [{ id: 'deepseek', baseUrl: 'https://api.deepseek.com/v1', models: [{ id: 'm', protocol: 'grpc' }] }],
    });
    expect(validateProvidersTree(bad).some((e) => e.field === 'protocol')).toBe(true);
  });
  it('左栏计数=providers 长度', () => {
    expect(normalizeProvidersTree(RAW).providers.length).toBe(2);
  });
});

describe('dirty 禁切 setActive', () => {
  it('diffActive 检测', () => {
    const a = normalizeProvidersTree(RAW);
    const b = normalizeProvidersTree({ ...RAW, activeModel: 'other' });
    expect(diffActive(a, b)).toBe(true);
    expect(diffActive(a, a)).toBe(false);
  });
});

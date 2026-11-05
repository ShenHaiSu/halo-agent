// src/Web.Client/src/components/settings/providerFormat.test.ts
// providerFormat 纯函数单测：单位格式化 / 过滤 / 使用中判定 / 请求头摘要 / 模型 id 去重。
import { describe, expect, it } from 'vitest';
import {
  canSetActive,
  filterModels,
  filterProviders,
  formatCtxLabel,
  formatOutLabel,
  hasModelId,
  headersCount,
  headersSummary,
  isActiveModel,
  isActiveProvider,
  normalizeDensity,
} from './providerFormat';
import { normalizeProvidersTree } from './providerEditing';

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
      headers: [{ key: 'X-Title', value: 'halo' }, { key: '  ', value: 'ignored' }],
      network: 'follow',
      models: [
        { id: 'deepseek-chat', displayName: '', protocol: 'chat', contextWindow: 128000, maxTokens: 20000 },
        { id: 'deepseek-reasoner', displayName: '思考版', protocol: 'responses', contextWindow: 1000000, maxTokens: 1500 },
      ],
    },
    {
      id: 'openai',
      displayName: 'OpenAI 官方',
      baseUrl: 'https://api.openai.com/v1',
      apiKeySet: false,
      headersEnabled: true,
      headers: [],
      network: 'proxy',
      models: [],
    },
  ],
};

describe('formatCtxLabel（上下文：M/K）', () => {
  it('≥1M 用 M，整数不带小数', () => {
    expect(formatCtxLabel(1000000)).toBe('1M');
    expect(formatCtxLabel(1500000)).toBe('1.5M');
    expect(formatCtxLabel(2000000)).toBe('2M');
  });
  it('K 档四舍五入', () => {
    expect(formatCtxLabel(128000)).toBe('128K');
    expect(formatCtxLabel(64000)).toBe('64K');
    expect(formatCtxLabel(1000)).toBe('1K');
  });
  it('<1000 原数，null/undefined 为 —', () => {
    expect(formatCtxLabel(999)).toBe('999');
    expect(formatCtxLabel(null)).toBe('—');
    expect(formatCtxLabel(undefined)).toBe('—');
  });
});

describe('formatOutLabel（输出：统一 K）', () => {
  it('≥1000 统一 K 单位', () => {
    expect(formatOutLabel(20000)).toBe('20K');
    expect(formatOutLabel(60000)).toBe('60K');
    expect(formatOutLabel(5000)).toBe('5K');
    expect(formatOutLabel(1500)).toBe('1.5K');
  });
  it('<1000 原数，null 为 —', () => {
    expect(formatOutLabel(800)).toBe('800');
    expect(formatOutLabel(null)).toBe('—');
  });
});

describe('过滤（左树只搜 Provider，密表搜 id/别称）', () => {
  const t = normalizeProvidersTree(RAW);
  it('空查询返回全部（新数组，不改原树）', () => {
    const r = filterProviders(t.providers, '   ');
    expect(r).toHaveLength(2);
    expect(r).not.toBe(t.providers);
  });
  it('按 id / 别称过滤 Provider', () => {
    expect(filterProviders(t.providers, 'DEEP').map((p) => p.id)).toEqual(['deepseek']);
    expect(filterProviders(t.providers, '官方').map((p) => p.id)).toEqual(['openai']);
  });
  it('按 id / 别称过滤模型，返回原下标', () => {
    const models = t.providers[0]!.models;
    expect(filterModels(models, '思考')).toEqual([1]);
    expect(filterModels(models, 'reasoner')).toEqual([1]);
    expect(filterModels(models, '')).toEqual([0, 1]);
  });
});

describe('使用中判定', () => {
  const t = normalizeProvidersTree(RAW);
  it('Provider / Model 使用中', () => {
    expect(isActiveProvider(t, 'deepseek')).toBe(true);
    expect(isActiveProvider(t, 'openai')).toBe(false);
    expect(isActiveModel(t, 'deepseek', 'deepseek-chat')).toBe(true);
    expect(isActiveModel(t, 'deepseek', 'deepseek-reasoner')).toBe(false);
    expect(isActiveModel(t, 'openai', 'deepseek-chat')).toBe(false);
  });
  it('空 Provider 不可设为使用中', () => {
    expect(canSetActive(t.providers[1])).toBe(false);
    expect(canSetActive(t.providers[0])).toBe(true);
    expect(canSetActive(undefined)).toBe(false);
  });
});

describe('请求头摘要', () => {
  it('空 key 过滤，无则「无」', () => {
    expect(headersSummary([{ key: 'X-Title', value: 'halo' }, { key: '', value: 'x' }])).toBe('X-Title');
    expect(headersSummary([{ key: 'X-A', value: '1' }, { key: 'X-B', value: '2' }])).toBe('X-A · X-B');
    expect(headersSummary([])).toBe('无');
    expect(headersSummary([{ key: '   ', value: 'v' }])).toBe('无');
  });
  it('计数与摘要口径一致', () => {
    expect(headersCount([{ key: 'A', value: '' }, { key: ' ', value: 'x' }])).toBe(1);
  });
});

describe('模型 id 去重（精确 Ordinal）', () => {
  it('编辑自身时排除自己', () => {
    const models = [{ id: 'deepseek-chat' }, { id: 'm2' }];
    expect(hasModelId(models, 'm2')).toBe(true);
    expect(hasModelId(models, 'm2', 1)).toBe(false);
    expect(hasModelId(models, 'M2')).toBe(false);
  });
});

describe('密度偏好归一化', () => {
  it('仅 roomy 生效，其余回落 dense', () => {
    expect(normalizeDensity('roomy')).toBe('roomy');
    expect(normalizeDensity('dense')).toBe('dense');
    expect(normalizeDensity(null)).toBe('dense');
    expect(normalizeDensity('x')).toBe('dense');
  });
});

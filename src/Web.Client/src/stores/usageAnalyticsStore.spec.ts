/**
 * need12：用量分析 Store（筛选条件 → 后端查询）单元测试。
 *
 * 覆盖 3 类历史缺陷：
 * 1. Provider 与 Model 交叉筛选条件恒假：后端 ApplyScope 对 provider 与 model 是 AND 关系
 *    （provider_id IN (...) AND (provider_id=P AND model_id=M)），
 *    换 Provider 后若仍保留别的 Provider 的 modelKeys → 查询恒空 → 图表空。
 * 2. 并发刷新竞态：先发起的"旧筛选条件"慢响应后到，会覆盖"新筛选条件"的结果
 *    → 图表显示上一次筛选的数据。
 * 3. setPreset 切换到非 custom 预设后必须清掉 customRange（否则 filter 里残留旧区间）。
 */
import { describe, expect, it, vi, beforeEach } from 'vitest';
import { createPinia, setActivePinia } from 'pinia';
import { nextTick } from 'vue';

const { bridgeStub } = vi.hoisted(() => ({
  bridgeStub: {
    getUsageFilterOptions: vi.fn(),
    getUsageOverview: vi.fn(),
    getUsageTrend: vi.fn(),
    getUsageProviderShare: vi.fn(),
    queryUsagePage: vi.fn(),
    getUsageDbStatus: vi.fn(),
  },
}));

vi.mock('@shared/bridge/hostBridge', async (importOriginal) => {
  const actual = await importOriginal<typeof import('@shared/bridge/hostBridge')>();
  return { ...actual, getHostBridge: () => bridgeStub };
});

import { useUsageAnalyticsStore } from './usageAnalyticsStore';

const SEP = '\x1f';
const KEY_P1_M1 = `p1${SEP}m1`;
const KEY_P2_M1 = `p2${SEP}m1`;

const FILTER_OPTIONS = {
  providers: [
    { id: 'p1', name: 'P1' },
    { id: 'p2', name: 'P2' },
  ],
  models: [
    { providerId: 'p1', providerName: 'P1', modelId: 'm1', display: 'm1', key: KEY_P1_M1, label: 'P1/m1' },
    { providerId: 'p2', providerName: 'P2', modelId: 'm1', display: 'm1', key: KEY_P2_M1, label: 'P2/m1' },
  ],
  sources: [{ id: 'agent', name: 'Agent 对话' }],
};

function trendOf(tag: string) {
  return {
    dates: ['2026-10-21'],
    perModel: [{ key: KEY_P1_M1, providerName: 'P1', modelId: 'm1', values: [tag === 'old' ? 1 : 2] }],
    hitRates: [0.1],
    totals: [tag === 'old' ? 1 : 2],
  };
}

describe('usageAnalyticsStore（用量分析筛选）', () => {
  beforeEach(() => {
    setActivePinia(createPinia());
    bridgeStub.getUsageFilterOptions.mockReset().mockResolvedValue({ ok: true, data: FILTER_OPTIONS });
    bridgeStub.getUsageOverview.mockReset().mockResolvedValue({
      ok: true,
      data: { totalRequests: 1, sumInput: 1, sumOutput: 1, sumCached: 0, hitRate: 0, activeModels: 1 },
    });
    bridgeStub.getUsageTrend.mockReset().mockResolvedValue({ ok: true, data: trendOf('new') });
    bridgeStub.getUsageProviderShare.mockReset().mockResolvedValue({ ok: true, data: [] });
    bridgeStub.queryUsagePage.mockReset().mockResolvedValue({
      ok: true,
      data: { total: 0, page: 1, pageSize: 10, rows: [] },
    });
    bridgeStub.getUsageDbStatus.mockReset().mockResolvedValue({ ok: true, data: null });
  });

  it('filter 组合全部筛选维度并透传给后端', async () => {
    const store = useUsageAnalyticsStore();
    await store.loadOptions();
    store.providerIds = ['p1'];
    store.modelKeys = [KEY_P1_M1];
    store.sources = ['agent'];
    store.estimatedOnly = false;
    store.preset = 'custom';
    store.customRange = ['2026-10-01', '2026-10-05'];

    expect(store.filter).toEqual({
      preset: 'custom',
      startDate: '2026-10-01',
      endDate: '2026-10-05',
      providerIds: ['p1'],
      modelKeys: [KEY_P1_M1],
      sources: ['agent'],
      estimatedOnly: false,
    });

    await store.refresh();
    const sent = bridgeStub.getUsageTrend.mock.calls[0][0] as Record<string, unknown>;
    expect(sent.providerIds).toEqual(['p1']);
    expect(sent.modelKeys).toEqual([KEY_P1_M1]);
    expect(sent.sources).toEqual(['agent']);
    expect(sent.estimatedOnly).toBe(false);
  });

  it('换 Provider 时剔除不属于该 Provider 的已选模型（否则后端 AND 条件恒假）', async () => {
    const store = useUsageAnalyticsStore();
    await store.loadOptions();
    store.modelKeys = [KEY_P2_M1];
    expect(store.modelKeys).toEqual([KEY_P2_M1]);

    store.providerIds = ['p1'];
    await nextTick();

    expect(store.modelKeys).toEqual([]);
    expect(store.modelOptions.map((m) => m.key)).toEqual([KEY_P1_M1]);
  });

  it('未选 Provider 时不清空已选模型', async () => {
    const store = useUsageAnalyticsStore();
    await store.loadOptions();
    store.modelKeys = [KEY_P2_M1];
    store.providerIds = [];
    await nextTick();
    expect(store.modelKeys).toEqual([KEY_P2_M1]);
  });

  it('并发刷新时旧响应不得覆盖新结果（图表不得回退到上一次筛选）', async () => {
    const store = useUsageAnalyticsStore();
    let call = 0;
    bridgeStub.getUsageTrend.mockImplementation(() => {
      call += 1;
      if (call === 1) {
        // 第一次（旧筛选条件）慢响应
        return new Promise((resolve) => setTimeout(() => resolve({ ok: true, data: trendOf('old') }), 30)) as Promise<{
          ok: boolean;
          data: unknown;
        }>;
      }
      return Promise.resolve({ ok: true, data: trendOf('new') }) as Promise<{ ok: boolean; data: unknown }>;
    });

    const first = store.refresh();
    store.preset = 'today';
    const second = store.refresh();
    await second;
    await first;

    expect(store.trend.perModel[0].values).toEqual([2]); // 新结果（值 2），不是旧结果（值 1）
    expect(store.loading).toBe(false);
  });

  it('setPreset 切到非 custom 预设时清空 customRange', async () => {
    const store = useUsageAnalyticsStore();
    store.customRange = ['2026-10-01', '2026-10-05'];
    store.setPreset('today');
    await nextTick();
    expect(store.customRange).toBeNull();
    expect(store.preset).toBe('today');
    expect(store.filter.startDate).toBeUndefined();
    expect(store.filter.endDate).toBeUndefined();
  });
});

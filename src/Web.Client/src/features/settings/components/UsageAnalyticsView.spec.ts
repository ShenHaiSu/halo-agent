/**
 * need12：用量分析（设置与诊断中心 → 用量分析）筛选 → 图表刷新 集成测试。
 *
 * 覆盖历史缺陷：
 * 1.【图表残留旧系列】ECharts setOption 默认是"合并"语义，筛选后被剔除的旧系列
 *    不会被移除，而是保留旧 data 继续渲染（堆叠趋势图出现已筛掉的模型 + 旧值柱子，
 *    legend 仍显示旧模型）。修复：整体替换(notMerge)。
 *    本测试用**真实 ECharts 实例**（SSR svg 模式，可在 jsdom 下运行）断言系列数与数据。
 * 2.【日期框与预设不同步】切到预设后日期框仍显示旧自定义区间 → 口径误导。
 * 3.【清空日期框无效】清空后 preset 仍是 custom + 旧区间 → 图表仍按旧区间渲染。
 */
import { describe, expect, it, vi, beforeEach, afterEach } from 'vitest';
import { mount, flushPromises } from '@vue/test-utils';
import { createPinia, setActivePinia, type Pinia } from 'pinia';
import naive, { NDatePicker } from 'naive-ui';
import { defineComponent, h } from 'vue';

const { chartInstances } = vi.hoisted(() => ({ chartInstances: [] as unknown[] }));

/**
 * 用真实 ECharts（SSR svg 模式，无需 canvas/DOM）替换组件里的 echarts.init，
 * 这样可以直接用 getOption() 断言"筛选后图表里还剩几个系列、数据是什么"。
 */
vi.mock('echarts/core', async () => {
  const real = await import('echarts');
  return {
    use: (...args: unknown[]) => (real as unknown as { use: (a: unknown) => void }).use(args[0]),
    init: () => {
      const c = (real as unknown as { init: (a: unknown, b: unknown, o: unknown) => unknown }).init(null, null, {
        renderer: 'svg',
        ssr: true,
        width: 800,
        height: 400,
      });
      chartInstances.push(c);
      return c;
    },
  };
});

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

import UsageAnalyticsView from './UsageAnalyticsView.vue';
import { useUsageAnalyticsStore } from '@/stores/usageAnalyticsStore';

const SEP = '\x1f';

/** 首次加载：3 个模型 × 3 天 */
const TREND_WIDE = {
  dates: ['2026-10-19', '2026-10-20', '2026-10-21'],
  perModel: [
    { key: `p1${SEP}m1`, providerName: 'P1', modelId: 'm1', values: [1, 2, 3] },
    { key: `p1${SEP}m2`, providerName: 'P1', modelId: 'm2', values: [4, 5, 6] },
    { key: `p2${SEP}m3`, providerName: 'P2', modelId: 'm3', values: [7, 8, 9] },
  ],
  hitRates: [0.1, 0.2, 0.3],
  totals: [12, 15, 18],
};
/** 筛选后：1 个模型 × 1 天 */
const TREND_NARROW = {
  dates: ['2026-10-21'],
  perModel: [{ key: `p1${SEP}m1`, providerName: 'P1', modelId: 'm1', values: [3] }],
  hitRates: [0.3],
  totals: [3],
};

const FILTER_OPTIONS = {
  providers: [
    { id: 'p1', name: 'P1' },
    { id: 'p2', name: 'P2' },
  ],
  models: [
    { providerId: 'p1', providerName: 'P1', modelId: 'm1', display: 'm1', key: `p1${SEP}m1`, label: 'P1/m1' },
    { providerId: 'p1', providerName: 'P1', modelId: 'm2', display: 'm2', key: `p1${SEP}m2`, label: 'P1/m2' },
    { providerId: 'p2', providerName: 'P2', modelId: 'm3', display: 'm3', key: `p2${SEP}m3`, label: 'P2/m3' },
  ],
  sources: [{ id: 'agent', name: 'Agent 对话' }],
};

type ChartLike = { getOption: () => { series?: Array<{ data?: unknown }>; xAxis?: Array<{ data?: unknown }> } };

const Host = defineComponent({
  setup() {
    return () => h(UsageAnalyticsView);
  },
});

async function mountView(pinia: Pinia) {
  const w = mount(Host, { global: { plugins: [naive, pinia] } });
  await flushPromises();
  await flushPromises();
  return w;
}

describe('UsageAnalyticsView（用量分析筛选 → 图表刷新）', () => {
  let pinia: Pinia;
  let trendCalls = 0;

  beforeEach(() => {
    pinia = createPinia();
    setActivePinia(pinia);
    chartInstances.length = 0;
    trendCalls = 0;
    bridgeStub.getUsageFilterOptions.mockReset().mockResolvedValue({ ok: true, data: FILTER_OPTIONS });
    bridgeStub.getUsageOverview.mockReset().mockResolvedValue({
      ok: true,
      data: { totalRequests: 3, sumInput: 30, sumOutput: 20, sumCached: 6, hitRate: 0.2, activeModels: 3 },
    });
    bridgeStub.getUsageTrend.mockReset().mockImplementation(() => {
      trendCalls += 1;
      return Promise.resolve({ ok: true, data: trendCalls === 1 ? TREND_WIDE : TREND_NARROW });
    });
    bridgeStub.getUsageProviderShare.mockReset().mockImplementation(() =>
      Promise.resolve({
        ok: true,
        data:
          trendCalls <= 1
            ? [
                { providerId: 'p1', name: 'P1', value: 30, share: 0.6 },
                { providerId: 'p2', name: 'P2', value: 20, share: 0.4 },
              ]
            : [{ providerId: 'p1', name: 'P1', value: 3, share: 1 }],
      })
    );
    bridgeStub.queryUsagePage.mockReset().mockResolvedValue({
      ok: true,
      data: { total: 0, page: 1, pageSize: 10, rows: [] },
    });
    bridgeStub.getUsageDbStatus.mockReset().mockResolvedValue({
      ok: true,
      data: { dbPath: 'usage.db', recordCount: 3, dbSizeBytes: 1024 },
    });
  });

  afterEach(() => {
    for (const c of chartInstances as Array<{ dispose?: () => void }>) {
      try {
        c.dispose?.();
      } catch {
        /* ignore */
      }
    }
  });

  it('初始渲染：堆叠图 3 个系列（按模型堆叠）', async () => {
    await mountView(pinia);
    const stacked = chartInstances[0] as ChartLike;
    expect(stacked.getOption().series?.length).toBe(3);
  });

  it('筛选后堆叠图不残留被筛掉的旧系列（旧数据必须消失）', async () => {
    const w = await mountView(pinia);
    const stacked = chartInstances[0] as ChartLike;
    expect(stacked.getOption().series?.length).toBe(3);

    // 切"今日"：后端只返回 1 个模型 × 1 天
    await w.find('[data-testid="usage-preset-today"]').trigger('click');
    await flushPromises();
    await flushPromises();

    const opt = stacked.getOption();
    expect(opt.series?.length).toBe(1);
    expect(opt.series?.[0].data).toEqual([3]);
    expect(opt.xAxis?.[0].data).toEqual(['2026-10-21']);
  });

  it('筛选后饼图只保留当前 Provider 的占比', async () => {
    const w = await mountView(pinia);
    const pie = chartInstances[2] as ChartLike;
    expect(pie.getOption().series?.[0].data).toHaveLength(2);

    await w.find('[data-testid="usage-preset-today"]').trigger('click');
    await flushPromises();
    await flushPromises();

    const data = pie.getOption().series?.[0].data as Array<{ value: number }>;
    expect(data).toHaveLength(1);
    expect(data[0].value).toBe(3);
  });

  it('切到预设后日期框被清空（不再显示旧的自定义区间）', async () => {
    const w = await mountView(pinia);
    const dp = w.findComponent(NDatePicker);
    const input = w.find('[data-testid="usage-custom-range"] input');

    // 先选一个自定义区间
    dp.vm.$emit('update:value', [new Date(2026, 9, 1).getTime(), new Date(2026, 9, 5).getTime()]);
    await flushPromises();
    const store = useUsageAnalyticsStore();
    expect(store.preset).toBe('custom');
    expect(store.filter.startDate).toBe('2026-10-01');
    expect((input.element as HTMLInputElement).value).not.toBe('');

    // 再切"今日" → 日期框必须清空
    await w.find('[data-testid="usage-preset-today"]').trigger('click');
    await flushPromises();
    expect(store.preset).toBe('today');
    expect(store.filter.startDate).toBeUndefined();
    expect((w.find('[data-testid="usage-custom-range"] input').element as HTMLInputElement).value).toBe('');
  });

  it('清空日期框回落到上一个预设（图表口径同步回落，不留旧 custom 区间）', async () => {
    const w = await mountView(pinia);
    const dp = w.findComponent(NDatePicker);
    const store = useUsageAnalyticsStore();

    dp.vm.$emit('update:value', [new Date(2026, 9, 1).getTime(), new Date(2026, 9, 5).getTime()]);
    await flushPromises();
    expect(store.preset).toBe('custom');

    // 清空日期框
    dp.vm.$emit('update:value', null);
    await flushPromises();

    expect(store.preset).toBe('last7');
    expect(store.customRange).toBeNull();
    expect(store.filter.startDate).toBeUndefined();
    expect(store.filter.endDate).toBeUndefined();
    // 回落必须触发重新查询（否则图表仍是旧区间的数据）
    expect(bridgeStub.getUsageTrend.mock.calls.length).toBeGreaterThan(1);
  });

  it('筛选后不重复重绘（notMerge 下重复 setOption 会重播动画、图表闪一下）', async () => {
    const w = await mountView(pinia);
    const stacked = chartInstances[0] as ChartLike & { setOption: (...a: unknown[]) => unknown };
    let calls = 0;
    const orig = stacked.setOption.bind(stacked);
    stacked.setOption = (...args: unknown[]) => {
      calls += 1;
      return orig(...args);
    };

    await w.find('[data-testid="usage-preset-today"]').trigger('click');
    await flushPromises();
    // 等 rAF 队列（queueRenderAll）跑完：重复渲染应被幂等守卫挡掉
    await new Promise((r) => setTimeout(r, 80));
    await flushPromises();

    expect(calls).toBe(1);
  });

  it('Provider/Model/来源/实测估算筛选都进入后端查询参数', async () => {
    const w = await mountView(pinia);
    const store = useUsageAnalyticsStore();
    store.providerIds = ['p1'];
    store.modelKeys = [`p1${SEP}m1`];
    store.sources = ['agent'];
    store.estimatedOnly = true;
    await w.find('[data-testid="usage-query-btn"]').trigger('click');
    await flushPromises();

    const sent = bridgeStub.getUsageTrend.mock.calls.at(-1)?.[0] as Record<string, unknown>;
    expect(sent.providerIds).toEqual(['p1']);
    expect(sent.modelKeys).toEqual([`p1${SEP}m1`]);
    expect(sent.sources).toEqual(['agent']);
    expect(sent.estimatedOnly).toBe(true);
  });
});

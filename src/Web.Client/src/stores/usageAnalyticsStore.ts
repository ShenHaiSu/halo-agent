import { defineStore } from 'pinia';
import { ref, computed } from 'vue';
import { getHostBridge, type UsageFilter, type UsagePreset, type UsageOverview, type UsageTrend, type UsageProviderShareItem, type UsagePage, type UsageFilterOptions } from '@/bridge/hostBridge';

export const USAGE_PRESETS: Array<{ key: UsagePreset; label: string; testid: string }> = [
  { key: 'today', label: '今日', testid: 'usage-preset-today' },
  { key: 'yesterday', label: '昨日', testid: 'usage-preset-yesterday' },
  { key: 'last3', label: '近3日', testid: 'usage-preset-last3' },
  { key: 'last7', label: '近7日', testid: 'usage-preset-last7' },
  { key: 'last30', label: '近30日', testid: 'usage-preset-last30' },
  { key: 'thisWeek', label: '本周', testid: 'usage-preset-thisWeek' },
  { key: 'thisMonth', label: '本月', testid: 'usage-preset-thisMonth' },
  { key: 'all', label: '全部', testid: 'usage-preset-all' },
];

export const useUsageAnalyticsStore = defineStore('usageAnalytics', () => {
  const preset = ref<UsagePreset>('last7');
  const customRange = ref<[string, string] | null>(null);
  const providerIds = ref<string[]>([]);
  const modelKeys = ref<string[]>([]);
  const metric = ref<'in' | 'out' | 'total'>('total');
  const page = ref(1);
  const pageSize = ref(10);

  const loading = ref(false);
  const error = ref('');
  const options = ref<UsageFilterOptions>({ providers: [], models: [] });
  const overview = ref<UsageOverview>({ totalRequests: 0, sumInput: 0, sumOutput: 0, sumCached: 0, hitRate: 0, activeModels: 0 });
  const trend = ref<UsageTrend>({ dates: [], perModel: [], hitRates: [], totals: [] });
  const share = ref<UsageProviderShareItem[]>([]);
  const pageData = ref<UsagePage>({ total: 0, page: 1, pageSize: 10, rows: [] });

  const filter = computed<UsageFilter>(() => {
    const f: UsageFilter = { preset: preset.value };
    if (preset.value === 'custom' && customRange.value) {
      f.startDate = customRange.value[0];
      f.endDate = customRange.value[1];
    }
    if (providerIds.value.length) f.providerIds = [...providerIds.value];
    if (modelKeys.value.length) f.modelKeys = [...modelKeys.value];
    return f;
  });

  const modelOptions = computed(() => {
    if (!providerIds.value.length) return options.value.models;
    return options.value.models.filter((m) => providerIds.value.includes(m.providerId));
  });

  async function loadOptions(): Promise<void> {
    try {
      const res = await getHostBridge().getUsageFilterOptions();
      if (res.ok && res.data) options.value = res.data;
    } catch { /* 降级空选项 */ }
  }

  async function refresh(): Promise<void> {
    loading.value = true;
    error.value = '';
    try {
      const bridge = getHostBridge();
      const f = filter.value;
      const [ov, tr, sh, pg] = await Promise.all([
        bridge.getUsageOverview(f),
        bridge.getUsageTrend(f, metric.value),
        bridge.getUsageProviderShare(f),
        bridge.queryUsagePage(f, page.value, pageSize.value),
      ]);
      if (ov.ok && ov.data) overview.value = ov.data;
      else if (!ov.ok && ov.code === 'USAGE_DB_NOT_READY') error.value = '用量库初始化失败，不影响对话';
      if (tr.ok && tr.data) trend.value = tr.data;
      if (sh.ok && sh.data) share.value = sh.data;
      if (pg.ok && pg.data) {
        pageData.value = pg.data;
      } else if (!pg.ok && pg.code === 'USAGE_PAGE_OVERFLOW') {
        page.value = 1;
        const retry = await bridge.queryUsagePage(f, 1, pageSize.value);
        if (retry.ok && retry.data) pageData.value = retry.data;
      }
      if (!ov.ok && ov.code && ov.code !== 'USAGE_DB_NOT_READY') error.value = ov.message || '查询失败';
    } catch (e) {
      error.value = e instanceof Error ? e.message : String(e);
    } finally {
      loading.value = false;
    }
  }

  function setPreset(p: UsagePreset): void {
    preset.value = p;
    if (p !== 'custom') customRange.value = null;
    page.value = 1;
    void refresh();
  }

  return {
    preset, customRange, providerIds, modelKeys, metric, page, pageSize,
    loading, error, options, overview, trend, share, pageData,
    filter, modelOptions, loadOptions, refresh, setPreset,
  };
});

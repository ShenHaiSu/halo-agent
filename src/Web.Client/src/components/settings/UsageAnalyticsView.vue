<template>
  <div class="space-y-3" data-testid="usage-tab">
    <!-- 筛选条 -->
    <div class="flex flex-wrap items-center gap-2">
      <n-button-group>
        <n-button
          v-for="p in USAGE_PRESETS"
          :key="p.key"
          size="tiny"
          :type="store.preset === p.key ? 'primary' : 'default'"
          :data-testid="p.testid"
          @click="store.setPreset(p.key)"
        >{{ p.label }}</n-button>
      </n-button-group>
      <n-date-picker
        v-model:value="rangeTs"
        type="daterange"
        size="small"
        clearable
        data-testid="usage-custom-range"
        style="width: 260px;"
        @update:value="onCustomRange"
      />
      <n-select
        v-model:value="store.providerIds"
        multiple
        clearable
        filterable
        :filter="filterProviderOption"
        size="small"
        placeholder="Provider"
        :options="providerOptions"
        data-testid="usage-provider-select"
        style="min-width: 160px; max-width: 240px;"
        @update:value="onScopeChange"
      />
      <n-select
        v-model:value="store.modelKeys"
        multiple
        clearable
        filterable
        :filter="filterModelOption"
        size="small"
        placeholder="Model"
        :options="modelOptions"
        data-testid="usage-model-select"
        style="min-width: 180px; max-width: 280px;"
        @update:value="onScopeChange"
      />
      <n-button size="small" type="primary" ghost data-testid="usage-query-btn" :loading="store.loading" @click="store.refresh()">查询</n-button>
      <div class="flex items-center gap-1.5 shrink-0 pl-1" data-testid="usage-mask-provider-block">
        <n-switch
          :value="privacy.settings.maskProviderName"
          size="small"
          data-testid="usage-mask-provider-switch"
          @update:value="onMaskProviderChange"
        />
        <span class="text-[11px] text-secondary select-none" title="隐藏真实服务商名称，仅显示 5 位随机代号">模糊 Provider</span>
      </div>
    </div>

    <n-alert v-if="store.error" type="warning" :show-icon="false" style="font-size: 12px;">{{ store.error }}</n-alert>

    <!-- 瓦片行 -->
    <div class="grid grid-cols-3 gap-2">
      <div class="rounded border border-subtle p-2" data-testid="usage-tile-requests">
        <div class="text-[11px] text-secondary">总请求数</div>
        <div class="text-lg font-semibold" style="font-variant-numeric: tabular-nums;">{{ fmtInt(store.overview.totalRequests) }}</div>
        <div class="text-[11px] text-secondary">所选范围内记录数</div>
      </div>
      <div class="rounded border border-subtle p-2" data-testid="usage-tile-input">
        <div class="text-[11px] text-secondary">总输入</div>
        <div class="text-lg font-semibold" style="font-variant-numeric: tabular-nums;" :title="fmtInt(store.overview.sumInput) + ' tokens'">{{ fmtToken(store.overview.sumInput) }}</div>
        <div class="text-[11px] text-secondary">tokens</div>
      </div>
      <div class="rounded border border-subtle p-2" data-testid="usage-tile-output">
        <div class="text-[11px] text-secondary">总生成</div>
        <div class="text-lg font-semibold" style="font-variant-numeric: tabular-nums;" :title="fmtInt(store.overview.sumOutput) + ' tokens'">{{ fmtToken(store.overview.sumOutput) }}</div>
        <div class="text-[11px] text-secondary">tokens</div>
      </div>
      <div class="rounded border border-subtle p-2" data-testid="usage-tile-cached">
        <div class="text-[11px] text-secondary">命中缓存</div>
        <div class="text-lg font-semibold" style="font-variant-numeric: tabular-nums;" :title="fmtInt(store.overview.sumCached) + ' tokens'">{{ fmtToken(store.overview.sumCached) }}</div>
        <div class="text-[11px] text-secondary">tokens</div>
      </div>
      <div class="rounded border border-subtle p-2" data-testid="usage-tile-hitrate">
        <div class="text-[11px] text-secondary">综合命中率</div>
        <div class="text-lg font-semibold" style="font-variant-numeric: tabular-nums;">{{ (store.overview.hitRate * 100).toFixed(1) }}%</div>
        <div class="text-[11px] text-secondary">Σcached/Σinput</div>
      </div>
      <div class="rounded border border-subtle p-2" data-testid="usage-tile-models">
        <div class="text-[11px] text-secondary">活跃模型</div>
        <div class="text-lg font-semibold" style="font-variant-numeric: tabular-nums;">{{ store.overview.activeModels }}</div>
        <div class="text-[11px] text-secondary">provider/model 去重数</div>
      </div>
    </div>

    <!-- 图1：用量趋势堆叠柱 -->
    <div class="rounded border border-subtle p-2">
      <div class="flex items-center justify-between mb-1">
        <span class="text-[13px] font-medium">用量趋势（按模型堆叠）</span>
        <n-radio-group v-model:value="store.metric" size="small" data-testid="usage-metric-switch" @update:value="store.refresh()">
          <n-radio-button value="in">输入</n-radio-button>
          <n-radio-button value="out">输出</n-radio-button>
          <n-radio-button value="total">总量</n-radio-button>
        </n-radio-group>
      </div>
      <div ref="stackedEl" data-testid="usage-chart-stacked" style="width: 100%; height: 260px;"></div>
      <n-empty v-if="!hasTrendData" size="small" description="暂无数据" />
    </div>

    <!-- 图2+图3 双列 -->
    <div class="grid grid-cols-2 gap-2">
      <div class="rounded border border-subtle p-2">
        <div class="text-[13px] font-medium mb-1">命中率趋势</div>
        <div ref="hitEl" data-testid="usage-chart-hit" style="width: 100%; height: 200px;"></div>
      </div>
      <div class="rounded border border-subtle p-2">
        <div class="text-[13px] font-medium mb-1">Provider 占比</div>
        <div ref="pieEl" data-testid="usage-chart-provider" style="width: 100%; height: 200px;"></div>
      </div>
    </div>

    <!-- 明细表 -->
    <div class="rounded border border-subtle p-2">
      <div class="text-[13px] font-medium mb-1">明细（{{ store.pageData.total }} 条）</div>
      <div class="overflow-x-auto">
        <table class="w-full text-[12px]" data-testid="usage-table">
          <thead>
            <tr class="text-left text-secondary border-b border-subtle">
              <th class="py-1 pr-2 font-medium">日期时间</th>
              <th class="py-1 pr-2 font-medium">Provider</th>
              <th class="py-1 pr-2 font-medium">Model</th>
              <th class="py-1 pr-2 font-medium text-right">输入</th>
              <th class="py-1 pr-2 font-medium text-right">输出</th>
              <th class="py-1 pr-2 font-medium text-right">命中缓存</th>
              <th class="py-1 pr-2 font-medium">命中率</th>
              <th class="py-1 font-medium">状态</th>
            </tr>
          </thead>
          <tbody>
            <tr v-for="r in store.pageData.rows" :key="r.id" class="border-b border-subtle hover:bg-elevated/50">
              <td class="py-1 pr-2 font-mono whitespace-nowrap">{{ r.requestDatetime }}</td>
              <td class="py-1 pr-2 font-mono" :title="displayTitle(r.providerName)">{{ displayName(r.providerName) }}</td>
              <td class="py-1 pr-2 font-mono">{{ r.modelId }}</td>
              <td class="py-1 pr-2 text-right font-mono">{{ fmtInt(r.inputTokens) }}</td>
              <td class="py-1 pr-2 text-right font-mono">{{ fmtInt(r.outputTokens) }}</td>
              <td class="py-1 pr-2 text-right font-mono">{{ fmtInt(r.cachedTokens) }}</td>
              <td class="py-1 pr-2">
                <span class="inline-flex items-center gap-1">
                  <span class="inline-block h-1.5 rounded" :style="{ width: '40px', background: hitColor(r.hitRate) }"></span>
                  <span class="font-mono">{{ (r.hitRate * 100).toFixed(1) }}%</span>
                </span>
              </td>
              <td class="py-1 font-mono text-secondary">{{ r.status }}</td>
            </tr>
            <tr v-if="!store.pageData.rows.length">
              <td colspan="8" class="py-4 text-center"><n-empty size="small" description="暂无数据" /></td>
            </tr>
          </tbody>
        </table>
      </div>
      <div class="flex justify-end mt-2">
        <n-pagination
          v-model:page="store.page"
          v-model:page-size="store.pageSize"
          :item-count="store.pageData.total"
          :page-sizes="[50, 100, 200]"
          show-size-picker
          size="small"
          data-testid="usage-pagination"
          @update:page="store.refresh()"
          @update:page-size="onPageSize"
        />
      </div>
      <div v-if="dbStatus" class="mt-1 text-[11px] text-secondary font-mono">库：{{ dbStatus.dbPath }} · {{ dbStatus.recordCount }} 条 · {{ dbSize }}</div>
    </div>
  </div>
</template>

<script setup lang="ts">
import { ref, computed, onMounted, onBeforeUnmount, watch, nextTick } from 'vue';
import { NButton, NButtonGroup, NSelect, NDatePicker, NRadioGroup, NRadioButton, NEmpty, NAlert, NPagination, NSwitch } from 'naive-ui';
import * as echarts from 'echarts/core';
import { BarChart, LineChart, PieChart } from 'echarts/charts';
import { GridComponent, TooltipComponent, LegendComponent, DatasetComponent } from 'echarts/components';
import { CanvasRenderer } from 'echarts/renderers';
import { useUsageAnalyticsStore, USAGE_PRESETS } from '@/stores/usageAnalyticsStore';
import { usePrivacyStore } from '@/stores/privacyStore';
import { getHostBridge, type UsageDbStatus } from '@/bridge/hostBridge';
import { formatTokenShort } from '@/utils/formatSmartBytes';
import { displayProviderName, displayProviderModel, displayProviderNameByName } from '@/utils/maskProvider';

echarts.use([BarChart, LineChart, PieChart, GridComponent, TooltipComponent, LegendComponent, DatasetComponent, CanvasRenderer]);

const store = useUsageAnalyticsStore();
const privacy = usePrivacyStore();
const masked = computed(() => privacy.settings.maskProviderName);
const stackedEl = ref<HTMLElement | null>(null);
const hitEl = ref<HTMLElement | null>(null);
const pieEl = ref<HTMLElement | null>(null);
let stackedChart: echarts.ECharts | null = null;
let hitChart: echarts.ECharts | null = null;
let pieChart: echarts.ECharts | null = null;
let ro: ResizeObserver | null = null;
const dbStatus = ref<UsageDbStatus | null>(null);

const providerOptions = computed(() => store.options.providers.map((p) => ({ label: displayProviderName(p.id, p.name, masked.value), value: p.id, realName: p.name })));
const modelOptions = computed(() => store.modelOptions.map((m) => ({ label: displayProviderModel(m.providerId, m.providerName, m.modelId, masked.value), value: m.key, realName: m.label })));
const hasTrendData = computed(() => store.trend.dates.length > 0 && store.trend.perModel.length > 0);

/** 下拉搜索：模糊开启时，代号和真名都能搜到（防“搜 Open 搜不到 O***”） */
function filterProviderOption(pattern: string, option: unknown): boolean {
  const o = option as { label?: string; realName?: string };
  const pat = (pattern || '').trim().toLowerCase();
  if (!pat) return true;
  const label = String(o?.label || '').toLowerCase();
  const real = String(o?.realName || '').toLowerCase();
  return label.includes(pat) || real.includes(pat);
}
function filterModelOption(pattern: string, option: unknown): boolean {
  const o = option as { label?: string; realName?: string };
  const pat = (pattern || '').trim().toLowerCase();
  if (!pat) return true;
  const label = String(o?.label || '').toLowerCase();
  const real = String(o?.realName || '').toLowerCase();
  return label.includes(pat) || real.includes(pat);
}

/** 明细表 Provider 列：模糊时显示代号，hover（title）可看真名 */
function displayName(realName: string): string {
  return displayProviderNameByName(realName, masked.value);
}
function displayTitle(realName: string): string {
  return masked.value ? realName : '';
}
function onMaskProviderChange(val: boolean): void {
  privacy.setMaskProviderName(val);
  renderAll();
}

function fmtInt(n: number): string {
  const v = Number.isFinite(n) ? Math.floor(n) : 0;
  return v.toLocaleString('en-US');
}
function fmtToken(n: number): string {
  return formatTokenShort(n);
}
function hitColor(h: number): string {
  if (h >= 0.6) return '#10b981';
  if (h >= 0.3) return '#f59e0b';
  return '#9ca3af';
}
const dbSize = computed(() => {
  if (!dbStatus.value) return '';
  const b = dbStatus.value.dbSizeBytes || 0;
  if (b < 1024) return b + ' B';
  if (b < 1048576) return (b / 1024).toFixed(1) + ' KB';
  return (b / 1048576).toFixed(2) + ' MB';
});

const rangeTs = ref<[number, number] | null>(null);
let scopeTimer: number | null = null;
function onScopeChange(): void {
  store.page = 1;
  if (scopeTimer) window.clearTimeout(scopeTimer);
  scopeTimer = window.setTimeout(() => { void store.refresh().then(() => renderAll()); }, 300);
}
function onPageSize(): void {
  store.page = 1;
  void store.refresh();
}
function onCustomRange(v: [number, number] | null): void {
  if (!v || v.length !== 2) return;
  const fmt = (ts: number): string => {
    const d = new Date(ts);
    const m = String(d.getMonth() + 1).padStart(2, '0');
    const day = String(d.getDate()).padStart(2, '0');
    return d.getFullYear() + '-' + m + '-' + day;
  };
  store.customRange = [fmt(v[0]), fmt(v[1])];
  store.preset = 'custom';
  store.page = 1;
  void store.refresh().then(() => renderAll());
}

const FONT = '"Microsoft YaHei", sans-serif';
function baseText(): Record<string, unknown> {
  return { fontFamily: FONT };
}

function renderStacked(): void {
  if (!stackedEl.value) return;
  if (!stackedChart) stackedChart = echarts.init(stackedEl.value, null, { renderer: 'canvas' });
  const dates = store.trend.dates;
  const maskOn = masked.value;
  const series = store.trend.perModel.map((s) => ({
    name: displayProviderModel('', s.providerName, s.modelId, maskOn),
    type: 'bar',
    stack: 'total',
    emphasis: { focus: 'series' },
    data: s.values,
  }));
  const pointCount = dates.length * Math.max(1, series.length);
  stackedChart.setOption({
    backgroundColor: 'transparent',
    textStyle: baseText(),
    tooltip: {
      trigger: 'axis',
      formatter: (params: unknown): string => {
        const arr = params as Array<{ seriesName: string; value: number; color: string }>;
        if (!arr || !arr.length) return '';
        const idx = (arr[0] as unknown as { dataIndex: number }).dataIndex;
        const date = dates[idx] || '';
        let html = date;
        let sum = 0;
        for (const p of arr) {
          const v = Number(p.value) || 0;
          sum += v;
          html += '<br/>' + p.seriesName + ': ' + v.toLocaleString('en-US');
        }
        const hr = store.trend.hitRates[idx];
        html += '<br/>合计: ' + sum.toLocaleString('en-US');
        if (hr !== undefined) html += ' · 命中率: ' + (hr * 100).toFixed(1) + '%';
        return html;
      },
    },
    legend: { type: 'scroll', textStyle: baseText() },
    grid: { left: 48, right: 12, top: 36, bottom: 24 },
    xAxis: { type: 'category', data: dates, axisLabel: { fontFamily: FONT, fontSize: 11 } },
    yAxis: {
      type: 'value',
      axisLabel: {
        fontFamily: FONT, fontSize: 11,
        formatter: (v: number): string => formatTokenShort(v),
      },
    },
    series,
    animation: pointCount < 500,
  });
}

function renderHit(): void {
  if (!hitEl.value) return;
  if (!hitChart) hitChart = echarts.init(hitEl.value, null, { renderer: 'canvas' });
  hitChart.setOption({
    backgroundColor: 'transparent',
    textStyle: baseText(),
    tooltip: {
      trigger: 'axis',
      formatter: (params: unknown): string => {
        const arr = params as Array<{ value: number; dataIndex: number }>;
        if (!arr || !arr.length) return '';
        const idx = (arr[0] as unknown as { dataIndex: number }).dataIndex;
        const date = store.trend.dates[idx] || '';
        const v = Number(arr[0].value) || 0;
        return date + '<br/>命中率: ' + (v * 100).toFixed(1) + '%';
      },
    },
    grid: { left: 40, right: 12, top: 12, bottom: 24 },
    xAxis: { type: 'category', data: store.trend.dates, axisLabel: { fontFamily: FONT, fontSize: 10 } },
    yAxis: {
      type: 'value', min: 0, max: 1,
      axisLabel: { fontFamily: FONT, fontSize: 10, formatter: (v: number): string => Math.round(v * 100) + '%' },
    },
    series: [{
      name: '命中率', type: 'line', data: store.trend.hitRates,
      markLine: { data: [{ type: 'average', name: '平均' }] },
      lineStyle: { color: '#10b981' }, itemStyle: { color: '#10b981' },
    }],
    animation: store.trend.dates.length < 500,
  });
}

function renderPie(): void {
  if (!pieEl.value) return;
  if (!pieChart) pieChart = echarts.init(pieEl.value, null, { renderer: 'canvas' });
  const maskOn = masked.value;
  pieChart.setOption({
    backgroundColor: 'transparent',
    textStyle: baseText(),
    tooltip: {
      trigger: 'item',
      formatter: (p: unknown): string => {
        const o = p as { name: string; value: number; percent: number };
        return o.name + '<br/>' + Number(o.value).toLocaleString('en-US') + ' tokens (' + Number(o.percent).toFixed(1) + '%)';
      },
    },
    legend: { bottom: 0, textStyle: baseText() },
    series: [{
      type: 'pie', radius: ['50%', '70%'],
      data: store.share.map((s) => ({ name: displayProviderNameByName(s.name, maskOn), value: s.value })),
      emphasis: { focus: 'series' },
      label: { fontFamily: FONT, fontSize: 11, formatter: '{b}\n{d}%' },
    }],
    animation: store.share.length < 500,
  });
}

function renderAll(): void {
  try {
    renderStacked();
    renderHit();
    renderPie();
  } catch { /* 图表渲染永不阻断面板 */ }
}

watch(() => [store.trend, store.share, store.metric], () => { renderAll(); }, { deep: true });
watch(masked, () => { renderAll(); });

onMounted(async () => {
  privacy.ensureLoaded();
  await store.loadOptions();
  await store.refresh();
  try {
    const res = await getHostBridge().getUsageDbStatus();
    if (res.ok && res.data) dbStatus.value = res.data;
  } catch { /* ignore */ }
  await nextTick();
  renderAll();
  try {
    ro = new ResizeObserver(() => {
      try {
        stackedChart?.resize();
        hitChart?.resize();
        pieChart?.resize();
      } catch { /* ignore */ }
    });
    if (stackedEl.value) ro.observe(stackedEl.value);
    if (hitEl.value) ro.observe(hitEl.value);
    if (pieEl.value) ro.observe(pieEl.value);
  } catch { /* observer 不可用则跳过 */ }
});

onBeforeUnmount(() => {
  try { ro?.disconnect(); } catch { /* ignore */ }
  try { stackedChart?.dispose(); } catch { /* ignore */ }
  try { hitChart?.dispose(); } catch { /* ignore */ }
  try { pieChart?.dispose(); } catch { /* ignore */ }
  stackedChart = null;
  hitChart = null;
  pieChart = null;
});

defineExpose({ refresh: () => store.refresh(), resize: () => renderAll() });
</script>

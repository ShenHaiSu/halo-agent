<!-- src/Web.Client/src/components/settings/pages/UsageAnalyticsPage.vue -->
<!-- P16 用量页：筛选表单 + 预设 + 汇总卡 + 3 图 + 明细表 + 治理区。查询态，isDirty 恒 false。 -->
<script setup lang="ts">
import { computed, onMounted, onUnmounted, ref } from 'vue';
import { transport } from '@/transport';
import { useToast } from '@/composables/useToast';
import { useSessionsStore } from '@/stores/sessions';
import { useStreamStore } from '@/stores/stream';
import { formatBytes, formatNumber } from '@/utils/format';
import HButton from '@/components/base/HButton.vue';
import HDialog from '@/components/base/HDialog.vue';
import HEmpty from '@/components/base/HEmpty.vue';
import HInput from '@/components/base/HInput.vue';
import FilterForm from '@/components/usage/FilterForm.vue';
import SummaryCards from '@/components/usage/SummaryCards.vue';
import DetailTable from '@/components/usage/DetailTable.vue';
import {
  createChart,
  renderDist,
  renderHitRate,
  renderTrend,
  type EChartsInstance,
} from '@/components/usage/charts/useUsageCharts';
import {
  fromInputValue,
  getPresetRange,
  daysAgoMidnightMs,
  toInputValue,
  type UsagePresetKey,
} from '@/components/usage/usagePresets';
import {
  buildProviderDict,
  type ProviderDict,
  type UsageQueryResult,
  type UsageRow,
  type UsageSummary,
} from '@/components/usage/usageTypes';

function isDirty(): boolean {
  return false;
}
function resetToSaved(): void {
  /* 查询态无脏数据 */
}
defineExpose({ isDirty, resetToSaved });

const toast = useToast();

// ---------- 筛选状态 ----------
const fromInput = ref('');
const toInput = ref('');
const providerId = ref('');
const modelId = ref('');
const sessionId = ref('');
const activePreset = ref<UsagePresetKey | null>('last7');
const page = ref(1);
const pageSize = 50;
const providerDict = ref<ProviderDict>({});

// ---------- 数据 ----------
const summary = ref<UsageSummary | null>(null);
const summaryFailed = ref(false);
const rows = ref<UsageRow[]>([]);
const total = ref(0);
const queryFailed = ref(false);
const querying = ref(false);
const degraded = ref(false);
const queryId = ref(0);
const stats = ref<{ sizeBytes: number; rowCount: number } | null>(null);

// ---------- 治理/告警 ----------
const showClearDialog = ref(false);
const showFullClear = ref(false);
const clearConfirmText = ref('');
const monthlyLimitInput = ref<string>(localStorage.getItem('halo.usage.monthlyReqLimit') ?? '');
const hitTipMinReq = ref(Number(localStorage.getItem('halo.usage.hitTip.minReq') ?? 50));
const hitTipMinRate = ref(Number(localStorage.getItem('halo.usage.hitTip.minRate') ?? 0.2));
const hideHitTip = ref(sessionStorage.getItem('halo.usage.hideHitTip') === '1');
const warnedThisSession = ref(sessionStorage.getItem('halo.usage.monthWarned') === '1');

function buildParams(withPage: boolean): Record<string, unknown> {
  const fromMs = fromInputValue(fromInput.value);
  const toMs = fromInputValue(toInput.value);
  const p: Record<string, unknown> = {
    tzOffsetMin: new Date().getTimezoneOffset(),
  };
  if (fromMs !== undefined) p.fromMs = fromMs;
  if (toMs !== undefined) p.toMs = toMs;
  const sid = sessionId.value.trim();
  if (providerId.value) p.providerId = providerId.value;
  if (modelId.value) p.modelId = modelId.value;
  if (sid) p.sessionId = sid;
  if (withPage) {
    p.page = page.value;
    p.pageSize = pageSize;
  }
  return p;
}

function validateDates(): boolean {
  const f = fromInputValue(fromInput.value);
  const t = fromInputValue(toInput.value);
  if (f !== undefined && t !== undefined && f > t) {
    toast.warning('开始时间不能晚于结束时间');
    return false;
  }
  return true;
}

async function runQuery(): Promise<void> {
  if (!validateDates()) return;
  const id = ++queryId.value;
  querying.value = true;
  const params = buildParams(false);
  const pageParams = buildParams(true);
  const [s, q] = await Promise.allSettled([
    transport.request<UsageSummary>('usage.summary', params),
    transport.request<UsageQueryResult>('usage.query', pageParams),
  ]);
  if (id !== queryId.value) return; // 旧响应丢弃
  querying.value = false;
  if (s.status === 'fulfilled') {
    summary.value = s.value;
    summaryFailed.value = false;
    degraded.value = s.value.degraded === true;
     checkMonthlyQuota();
  } else {
    summaryFailed.value = true;
    toast.error('汇总加载失败，稍后重试');
  }
  if (q.status === 'fulfilled') {
    rows.value = q.value.items ?? [];
    total.value = q.value.total ?? 0;
    queryFailed.value = false;
    if (q.value.degraded === true) degraded.value = true;
  } else {
    queryFailed.value = true;
    toast.error('明细加载失败，稍后重试');
  }
  if (summary.value) redrawCharts();
  void loadStats();
}

async function loadStats(): Promise<void> {
  try {
    const r = await transport.request<{ sizeBytes: number; rowCount: number }>('usage.stats', null);
    stats.value = { sizeBytes: r.sizeBytes ?? 0, rowCount: r.rowCount ?? 0 };
  } catch {
    /* 治理区失败不打扰主流程 */
  }
}

// ---------- 预设/重置（点击原子 3 步） ----------
async function onPreset(key: UsagePresetKey): Promise<void> {
  const keepDims = key === 'all';
  const prevProvider = providerId.value;
  const prevModel = modelId.value;
  const prevSession = sessionId.value;
  const r = getPresetRange(key);
  fromInput.value = toInputValue(r.fromMs);
  toInput.value = toInputValue(r.toMs);
  activePreset.value = key;
  if (keepDims) {
    providerId.value = prevProvider;
    modelId.value = prevModel;
    sessionId.value = prevSession;
  }
  page.value = 1;
  await runQuery();
}

async function onReset(): Promise<void> {
  fromInput.value = '';
  toInput.value = '';
  providerId.value = '';
  modelId.value = '';
  sessionId.value = '';
  activePreset.value = null;
  page.value = 1;
  await runQuery();
}

function onDateEdit(which: 'from' | 'to', v: string): void {
  if (which === 'from') fromInput.value = v;
  else toInput.value = v;
  // 手改任一即取消预设高亮
  activePreset.value = null;
}

async function onPage(p: number): Promise<void> {
  page.value = p;
  await runQuery();
}

// ---------- 缓存优化提示（7.1） ----------
const hitTip = computed(() => {
  if (hideHitTip.value || !summary.value || summaryFailed.value) return null;
  const minReq = hitTipMinReq.value > 0 ? hitTipMinReq.value : 50;
  const minRate = hitTipMinRate.value >= 0 ? hitTipMinRate.value : 0.2;
  for (const m of summary.value.byModel) {
    if (m.requests >= minReq && m.hitRate < minRate) {
      // 仅当当前筛选含该行（provider/model 过滤一致）
      if (providerId.value && providerId.value !== m.providerId) continue;
      if (modelId.value && modelId.value !== m.modelId) continue;
      return m;
    }
  }
  return null;
});

function dismissHitTip(): void {
  hideHitTip.value = true;
  sessionStorage.setItem('halo.usage.hideHitTip', '1');
}

function saveHitTipThresholds(): void {
  localStorage.setItem('halo.usage.hitTip.minReq', String(hitTipMinReq.value));
  localStorage.setItem('halo.usage.hitTip.minRate', String(hitTipMinRate.value));
  toast.success('提示阈值已保存');
}

// ---------- 配额告警（7.4，次数阈值） ----------
 function checkMonthlyQuota(): void {
   const raw = (localStorage.getItem('halo.usage.monthlyReqLimit') ?? '').trim();
   if (!raw) return;
   const limit = Number(raw);
   if (!Number.isInteger(limit) || limit <= 0) return;
   // 本月口径与 05 本月一致：本地 1 日 00:00 – 现在
   const now = new Date();
   if (warnedThisSession.value) return;
   // summary 本身是当前筛选口径；告警按本月全量口径比较
  void transport
    .request<UsageSummary>('usage.summary', {
      fromMs: new Date(now.getFullYear(), now.getMonth(), 1).getTime(),
      tzOffsetMin: new Date().getTimezoneOffset(),
    })
    .then((ms) => {
      if (ms.requests > limit && !warnedThisSession.value) {
        warnedThisSession.value = true;
        sessionStorage.setItem('halo.usage.monthWarned', '1');
        toast.warning(`本月请求数已达 ${formatNumber(ms.requests)} 次，超过阈值 ${formatNumber(limit)} 次`);
      }
    })
    .catch(() => {});
}

function saveMonthlyLimit(): void {
  const v = monthlyLimitInput.value.trim();
  if (v && (!Number.isInteger(Number(v)) || Number(v) <= 0)) {
    toast.warning('阈值须为正整数，清空则关闭告警');
    return;
  }
  if (!v) localStorage.removeItem('halo.usage.monthlyReqLimit');
  else localStorage.setItem('halo.usage.monthlyReqLimit', v);
  warnedThisSession.value = false;
  sessionStorage.removeItem('halo.usage.monthWarned');
  toast.success('配额阈值已保存');
}

// ---------- 会话下钻（7.2） ----------
async function onDrilldown(sid: string): Promise<void> {
  const stream = useStreamStore();
  if (stream.busy) {
    toast.warning('生成中，暂不能跳转，已回填筛选框');
    sessionId.value = sid;
    activePreset.value = null;
    page.value = 1;
    await runQuery();
    return;
  }
  try {
    await useSessionsStore().select(sid);
  } catch {
    toast.error('跳转会话失败，已回填筛选框');
    sessionId.value = sid;
    page.value = 1;
    await runQuery();
  }
}

// ---------- 导出 CSV（7.3） ----------
async function onExport(): Promise<void> {
  if (!validateDates()) return;
  try {
    const r = await transport.request<{ csv: string; truncated: boolean }>(
      'usage.export',
      buildParams(false),
    );
    if (r.truncated) toast.warning('已截断，请缩小范围');
    const blob = new Blob(['\uFEFF' + (r.csv ?? ''),], { type: 'text/csv;charset=utf-8' });
    const d = new Date();
    const pad = (n: number): string => String(n).padStart(2, '0');
    const name = `usage-${d.getFullYear()}${pad(d.getMonth() + 1)}${pad(d.getDate())}-${pad(d.getHours())}${pad(d.getMinutes())}.csv`;
    const url = URL.createObjectURL(blob);
    const a = document.createElement('a');
    a.href = url;
    a.download = name;
    document.body.appendChild(a);
    a.click();
    a.remove();
    setTimeout(() => URL.revokeObjectURL(url), 5000);
  } catch {
    toast.error('导出失败，稍后重试');
  }
}

// ---------- 数据治理（7.5） ----------
async function onClear90(): Promise<void> {
  showClearDialog.value = true;
  showFullClear.value = false;
  clearConfirmText.value = '';
}

async function confirmClear90(): Promise<void> {
  try {
    const r = await transport.request<{ removed: number }>('usage.clear', {
      beforeMs: daysAgoMidnightMs(90),
    });
    toast.success(`已清理 ${formatNumber(r.removed ?? 0)} 条`);
    showClearDialog.value = false;
    await runQuery();
  } catch {
    toast.error('清理失败，稍后重试');
  }
}

async function confirmFullClear(): Promise<void> {
  if (clearConfirmText.value.trim() !== 'CLEAR') {
    toast.warning('请输入 CLEAR 确认全部清空');
    return;
  }
  try {
    const r = await transport.request<{ removed: number }>('usage.clear', { beforeMs: Date.now() });
    toast.success(`已清空 ${formatNumber(r.removed ?? 0)} 条`);
    showClearDialog.value = false;
    showFullClear.value = false;
    clearConfirmText.value = '';
    await runQuery();
  } catch {
    toast.error('清空失败，稍后重试');
  }
}

// ---------- 图表 ----------
let trendCtl: { chart: EChartsInstance; dispose: () => void } | null = null;
let distCtl: { chart: EChartsInstance; dispose: () => void } | null = null;
let hitCtl: { chart: EChartsInstance; dispose: () => void } | null = null;
const trendEl = ref<HTMLElement | null>(null);
const distEl = ref<HTMLElement | null>(null);
const hitEl = ref<HTMLElement | null>(null);

 function redrawCharts(): void {
   const s = summary.value;
   if (!s || summaryFailed.value) return;
   if (trendCtl) renderTrend(trendCtl.chart, s.byDay, trendEl.value);
   if (distCtl) renderDist(distCtl.chart, s.byModel, distEl.value);
   if (hitCtl) renderHitRate(hitCtl.chart, s.byDay, hitEl.value);
 }

const bothFailed = computed(() => summaryFailed.value && queryFailed.value);

onMounted(async () => {
  if (trendEl.value) trendCtl = createChart(trendEl.value);
  if (distEl.value) distCtl = createChart(distEl.value);
  if (hitEl.value) hitCtl = createChart(hitEl.value);
  // 首屏：先拿无过滤 summary 建全量词典并缓存，筛选后不重建
  try {
    const full = await transport.request<UsageSummary>('usage.summary', {
      tzOffsetMin: new Date().getTimezoneOffset(),
    });
    providerDict.value = buildProviderDict(full.byModel ?? []);
  } catch {
    providerDict.value = {};
  }
  // 默认近七日
  const r = getPresetRange('last7');
  fromInput.value = toInputValue(r.fromMs);
  toInput.value = toInputValue(r.toMs);
  activePreset.value = 'last7';
  await runQuery();
});

onUnmounted(() => {
  trendCtl?.dispose();
  distCtl?.dispose();
  hitCtl?.dispose();
  trendCtl = distCtl = hitCtl = null;
});
</script>

<template>
  <div class="flex flex-col gap-3 p-1">
    <FilterForm
      :from-input="fromInput"
      :to-input="toInput"
      :provider-id="providerId"
      :model-id="modelId"
      :session-id="sessionId"
      :provider-dict="providerDict"
      :active-preset="activePreset"
      :querying="querying"
      :disabled="degraded"
      @update:from-input="onDateEdit('from', $event)"
      @update:to-input="onDateEdit('to', $event)"
      @update:provider-id="providerId = $event"
      @update:model-id="modelId = $event"
      @update:session-id="sessionId = $event"
      @preset="onPreset"
      @submit="runQuery"
      @reset="onReset"
      @export="onExport"
    />

    <SummaryCards :summary="summary" :failed="summaryFailed" />

    <div v-if="hitTip" class="rounded-md border border-border-subtle bg-bg-elevated px-3 py-2 text-xs text-fg-muted" role="status">
      {{ hitTip.providerId }}/{{ hitTip.modelId }} 缓存命中率低（{{ (hitTip.hitRate * 100).toFixed(1) }}%），提示词前缀抖动可能偏大，固定 system prompt 前缀可提升命中。
      <button type="button" class="ml-2 text-accent hover:underline" @click="dismissHitTip">关闭</button>
    </div>

    <div v-if="bothFailed" class="py-8 text-center">
      <HEmpty title="用量加载失败" description="汇总与明细均加载失败，请检查本地库后重试。" />
      <div class="mt-2"><HButton variant="secondary" size="sm" @click="runQuery">重试</HButton></div>
    </div>
    <template v-else>
      <div class="relative">
        <div v-if="degraded" class="pointer-events-none absolute inset-0 z-10 flex items-center justify-center" aria-hidden="true">
          <span class="rounded bg-bg px-2 py-1 text-xs text-fg-subtle opacity-80">本地库不可用</span>
        </div>
        <div :class="degraded ? 'opacity-50 grayscale' : ''" class="flex flex-col gap-3">
          <section aria-label="用量趋势">
            <h3 class="mb-1 text-xs font-medium text-fg-muted">趋势</h3>
            <div ref="trendEl" style="height: 280px" role="img" aria-label="用量趋势图" />
          </section>
          <div class="usage-duo">
            <section aria-label="模型分布" class="min-w-0 flex-1">
              <h3 class="mb-1 text-xs font-medium text-fg-muted">分布</h3>
              <div ref="distEl" style="height: 240px" role="img" aria-label="模型分布图" />
            </section>
            <section aria-label="命中率" class="min-w-0 flex-1">
              <h3 class="mb-1 text-xs font-medium text-fg-muted">命中率</h3>
              <div ref="hitEl" style="height: 240px" role="img" aria-label="命中率图" />
            </section>
          </div>
        </div>
      </div>

      <DetailTable
        :rows="rows"
        :total="total"
        :page="page"
        :page-size="pageSize"
        :loading="querying"
        :failed="queryFailed"
        @update:page="onPage"
        @drilldown="onDrilldown"
      />
    </template>

    <section aria-label="数据治理" class="rounded-md border border-border-subtle p-3">
      <h3 class="mb-2 text-sm font-medium text-fg">数据治理</h3>
      <div class="mb-2 text-xs text-fg-muted">
        库大小 {{ stats ? formatBytes(stats.sizeBytes) : '—' }} · {{ stats ? formatNumber(stats.rowCount) : '—' }} 条
      </div>
      <div class="mb-2 flex flex-wrap items-center gap-2">
        <HButton variant="secondary" size="sm" @click="onClear90">清理 90 天前数据</HButton>
        <label class="flex items-center gap-1 text-xs text-fg-muted">
          本月请求数阈值
          <HInput v-model="monthlyLimitInput" size="sm" placeholder="空=关闭" class="w-28" />
        </label>
        <HButton variant="ghost" size="sm" @click="saveMonthlyLimit">保存阈值</HButton>
      </div>
      <div class="flex flex-wrap items-center gap-2 text-xs text-fg-muted">
        <label class="flex items-center gap-1">
           命中提示 ≥ <HInput :model-value="hitTipMinReq" size="sm" type="number" class="w-20" @update:model-value="hitTipMinReq = Number($event)" /> 次且命中率 &lt;
           <HInput :model-value="hitTipMinRate" size="sm" type="number" class="w-20" @update:model-value="hitTipMinRate = Number($event)" />
        </label>
        <HButton variant="ghost" size="sm" @click="saveHitTipThresholds">保存</HButton>
      </div>
    </section>

    <HDialog :open="showClearDialog" title="清理用量数据？" width="sm" @update:open="showClearDialog = $event">
      <p class="text-sm">将删除 90 天前的数据，该操作不可恢复。</p>
      <button
        v-if="stats && stats.rowCount > 0 && !showFullClear"
        type="button"
        class="mt-2 text-xs text-danger hover:underline"
        @click="showFullClear = true"
      >
        全部清空
      </button>
      <div v-if="showFullClear" class="mt-2 flex flex-col gap-2">
        <p class="text-xs text-danger">危险操作：请输入 CLEAR 确认清空全部数据。</p>
        <HInput v-model="clearConfirmText" size="sm" placeholder="CLEAR" />
        <div><HButton variant="danger" size="sm" @click="confirmFullClear">确认全部清空</HButton></div>
      </div>
      <template #footer>
        <HButton variant="secondary" size="sm" @click="showClearDialog = false">取消</HButton>
        <HButton variant="danger" size="sm" @click="confirmClear90">清理 90 天前</HButton>
      </template>
    </HDialog>
  </div>
</template>

<style scoped>
.usage-duo {
  display: flex;
  gap: 0.75rem;
}
@media (max-width: 900px) {
  .usage-duo {
    flex-direction: column;
  }
}
</style>

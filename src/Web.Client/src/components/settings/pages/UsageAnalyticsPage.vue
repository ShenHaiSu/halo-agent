<!-- src/Web.Client/src/components/settings/pages/UsageAnalyticsPage.vue -->
<!-- P17 用量页：筛选表单 + 预设 + 汇总卡 + 主图（双轴） + 饼图 + 明细表 + 治理区（三子卡 + 危险区）。查询态，isDirty 恒 false。 -->
<script setup lang="ts">
import { computed, onMounted, onUnmounted, ref } from 'vue';
import { transport } from '@/transport';
import { hasHost } from '@/bridge/bind';
import { hostBridge } from '@/bridge/host';
import { useToast } from '@/composables/useToast';
import { copyText } from '@/utils/clipboard';
import { formatBytes, formatZhCount } from '@/utils/format';
import HButton from '@/components/base/HButton.vue';
import HDialog from '@/components/base/HDialog.vue';
import HEmpty from '@/components/base/HEmpty.vue';
import HInput from '@/components/base/HInput.vue';
import FilterForm from '@/components/usage/FilterForm.vue';
import SummaryCards from '@/components/usage/SummaryCards.vue';
import DetailTable from '@/components/usage/DetailTable.vue';
import {
  isMaskEnabled,
  resolveProviderDisplay,
  setMaskEnabled,
} from '@/components/usage/providerMask';
import {
  createChart,
  renderModelPie,
  renderTrend,
  type EChartsInstance,
} from '@/components/usage/charts/useUsageCharts';
import {
  fromInputValue,
  getPresetRange,
  daysAgoMidnightMs,
  toInputValue,
  toEndInputValue,
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
const activePreset = ref<UsagePresetKey | null>('last7');
const page = ref(1);
const pageSize = ref(20);
const PAGE_SIZE_OPTIONS = [20, 50, 100, 200];
const providerDict = ref<ProviderDict>({});
// P17-04 隐私遮蔽：默认开，切换只重绘 + 重渲染（不重查）。
const maskOn = ref(isMaskEnabled());

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
// 存回合数阈值，key 名保留兼容
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
  // 过滤恒用真值：遮蔽只做显示映射，绝不回写请求参数。
  if (providerId.value) p.providerId = providerId.value;
  if (modelId.value) p.modelId = modelId.value;
  if (withPage) {
    p.page = page.value;
    p.pageSize = pageSize.value;
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
    let t = q.value.total ?? 0;
    let items = q.value.items ?? [];
    const tp = Math.max(1, Math.ceil(t / pageSize.value));
    // 当前页越界（如筛选收窄 / 切大 pageSize 后总页数变小）→ 自动回退到最后一页并重取一次
    if (page.value > tp) {
      page.value = tp;
      querying.value = true;
      try {
        const q2 = await transport.request<UsageQueryResult>('usage.query', buildParams(true));
        if (id !== queryId.value) return;
        t = q2.total ?? 0;
        items = q2.items ?? [];
        if (q2.degraded === true) degraded.value = true;
      } catch {
        queryFailed.value = true;
        toast.error('明细加载失败，稍后重试');
        if (summary.value) redrawCharts();
        void loadStats();
        querying.value = false;
        return;
      } finally {
        if (id === queryId.value) querying.value = false;
      }
    }
    rows.value = items;
    total.value = t;
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
  const r = getPresetRange(key);
  fromInput.value = toInputValue(r.fromMs);
  toInput.value = toEndInputValue(r.toMs);
  activePreset.value = key;
  if (keepDims) {
    providerId.value = prevProvider;
    modelId.value = prevModel;
  }
  page.value = 1;
  await runQuery();
}

async function onReset(): Promise<void> {
  fromInput.value = '';
  toInput.value = '';
  providerId.value = '';
  modelId.value = '';
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

async function onPageSize(ps: number): Promise<void> {
  const next = PAGE_SIZE_OPTIONS.includes(ps) ? ps : 20;
  if (next === pageSize.value) return;
  pageSize.value = next;
  // 用上次 total 预夹当前页，避免切大页长后停在空页；runQuery 内还会按最新 total 二次兜底。
  const tp = Math.max(1, Math.ceil((total.value ?? 0) / pageSize.value));
  if (page.value > tp) page.value = tp;
  await runQuery();
}

// ---------- 隐私遮蔽开关（只重绘 + 重渲染，不重查） ----------
function onMaskToggle(v: boolean): void {
  setMaskEnabled(v);
  maskOn.value = v;
  redrawCharts();
}

/** provider 显示统一走 resolve（下拉/饼图/横幅复用同一假名）。 */
function providerName(pid: string): string {
  return resolveProviderDisplay(pid, providerDict.value[pid]?.display || pid);
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

const hitTipLabel = computed(() => {
  const m = hitTip.value;
  if (!m) return '';
  void maskOn.value; // 开关订阅：localStorage 非响应式，靠 prop/ref 变化触发重算
  return `${resolveProviderDisplay(m.providerId, m.providerDisplay ?? '')}/${m.modelId}`;
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
  // 本口径与 05 本月一致：本地 1 日 00:00 – 现在
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
        toast.warning(
          `本月回合数已达 ${formatZhCount(ms.requests)} 次，超过阈值 ${formatZhCount(limit)} 次`
        );
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

// ---------- 导出 CSV（P17-06：桌面选路→引擎落盘→reveal；浏览器走 Blob 老路） ----------
function suggestedName(): string {
  const d = new Date();
  const pad = (n: number): string => String(n).padStart(2, '0');
  return `usage-${d.getFullYear()}${pad(d.getMonth() + 1)}${pad(d.getDate())}-${pad(d.getHours())}${pad(d.getMinutes())}.csv`;
}

/** 浏览器老路：usage.export → Blob 下载（桌面 CefSharp 内被吞，禁用）。 */
async function legacyBlobDownload(): Promise<void> {
  const r = await transport.request<{ csv: string; truncated: boolean }>(
    'usage.export',
    buildParams(false)
  );
  if (r.truncated) toast.warning('已截断，请缩小范围');
  const blob = new Blob(['\uFEFF' + (r.csv ?? '')], { type: 'text/csv;charset=utf-8' });
  const url = URL.createObjectURL(blob);
  const a = document.createElement('a');
  a.href = url;
  a.download = suggestedName();
  document.body.appendChild(a);
  a.click();
  a.remove();
  setTimeout(() => URL.revokeObjectURL(url), 5000);
}

async function onExport(): Promise<void> {
  if (!validateDates()) return;
  // 桌面包 transport.isMock 恒 false，以 hasHost() 为准；bind 未完成前误判走 Blob 会被吞，故桌面首屏须先 bind。
  if (!hasHost()) {
    try {
      await legacyBlobDownload();
    } catch {
      toast.error('导出失败，稍后重试');
    }
    return;
  }
  try {
    const savePath = await transport.request<string | null>('fs.pickSavePath', {
      defaultName: suggestedName(),
      filter: 'CSV 文件 (*.csv)|*.csv',
    });
    if (!savePath) return; // 取消静默
    // 只传 maskMap，不传布尔；provider<100 全量传，超限截断并 toast 降级写真名。
    let maskMap: Record<string, string> | undefined;
    let maskDegraded = false;
    if (isMaskEnabled()) {
      const pids = Object.keys(providerDict.value).sort();
      if (pids.length > 100) maskDegraded = true;
      maskMap = {};
      for (const pid of pids.slice(0, 100)) maskMap[pid] = providerName(pid);
    }
    const r = await transport.request<{ path: string; wrote: number; truncated: boolean }>(
      'usage.exportFile',
      { ...buildParams(false), path: savePath, maskMap }
    );
    if (r.truncated) toast.warning('已截断，请缩小范围');
    if (maskDegraded) toast.warning('服务商过多，隐私遮蔽降级为写真名');
    toast.success(`已导出 ${formatZhCount(r.wrote)} 条 → ${r.path}`, {
      action: { label: '打开所在文件夹', onClick: () => void hostBridge.showItemInFolder(r.path) },
    });
    // reveal 降级不可达（callOrMock 吞异常永不 reject），故 toast 常带路径 + 预复制路径；
    // 写盘后文件被删导致 ShowItemInFolder 失败时，用户仍可手动按路径查找。
    void copyText(r.path);
    await hostBridge.showItemInFolder(r.path);
  } catch {
    toast.error('导出失败，稍后重试');
  }
}

// ---------- 数据治理（P17-05：三子卡 + 危险区；两条清理路径共用一 HDialog） ----------
async function onClear90(): Promise<void> {
  showClearDialog.value = true;
  showFullClear.value = false;
  clearConfirmText.value = '';
}

/** 危险区触发：置两 flag，切到全部清空语义。 */
async function onFullClear(): Promise<void> {
  showClearDialog.value = true;
  showFullClear.value = true;
  clearConfirmText.value = '';
}

async function confirmClear90(): Promise<void> {
  try {
    const r = await transport.request<{ removed: number }>('usage.clear', {
      beforeMs: daysAgoMidnightMs(90),
    });
    toast.success(`已清理 ${formatZhCount(r.removed ?? 0)} 条`);
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
    toast.success(`已清空 ${formatZhCount(r.removed ?? 0)} 条`);
    showClearDialog.value = false;
    showFullClear.value = false;
    clearConfirmText.value = '';
    await runQuery();
  } catch {
    toast.error('清空失败，稍后重试');
  }
}

// ---------- 图表（P17-02：主图双轴 + 模型饼图；命中率小图已下线） ----------
let trendCtl: { chart: EChartsInstance; dispose: () => void } | null = null;
let pieCtl: { chart: EChartsInstance; dispose: () => void } | null = null;
const trendEl = ref<HTMLElement | null>(null);
const pieEl = ref<HTMLElement | null>(null);

const byDayEmpty = computed(() => (summary.value?.byDay.length ?? 0) === 0);
const byModelEmpty = computed(() => (summary.value?.byModel.length ?? 0) === 0);

function redrawCharts(): void {
  const s = summary.value;
  if (!s || summaryFailed.value) return;
  if (trendCtl) renderTrend(trendCtl.chart, s.byDay, trendEl.value);
  if (pieCtl) {
    if (s.byModel.length === 0)
      pieCtl.chart.clear(); // 空数据不 init 空饼
    else
      renderModelPie(pieCtl.chart, s.byModel, pieEl.value, { resolve: (pid) => providerName(pid) });
  }
}

const bothFailed = computed(() => summaryFailed.value && queryFailed.value);

onMounted(async () => {
  if (trendEl.value) trendCtl = createChart(trendEl.value);
  if (pieEl.value) pieCtl = createChart(pieEl.value);
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
  toInput.value = toEndInputValue(r.toMs);
  activePreset.value = 'last7';
  await runQuery();
});

onUnmounted(() => {
  trendCtl?.dispose();
  pieCtl?.dispose();
  trendCtl = pieCtl = null;
});
</script>

<template>
  <div class="flex flex-col gap-3 p-1">
    <FilterForm
      :from-input="fromInput"
      :to-input="toInput"
      :provider-id="providerId"
      :model-id="modelId"
      :provider-dict="providerDict"
      :active-preset="activePreset"
      :querying="querying"
      :mask-on="maskOn"
      :disabled="degraded"
      @update:from-input="onDateEdit('from', $event)"
      @update:to-input="onDateEdit('to', $event)"
      @update:provider-id="providerId = $event"
      @update:model-id="modelId = $event"
      @update:mask-on="onMaskToggle"
      @preset="onPreset"
      @submit="runQuery"
      @reset="onReset"
      @export="onExport"
    />

    <SummaryCards :summary="summary" :failed="summaryFailed" />

    <div
      v-if="hitTip"
      class="rounded-md border border-border-subtle bg-bg-elevated px-3 py-2 text-xs text-fg-muted"
      role="status"
    >
      {{ hitTipLabel }} 缓存命中率低（{{
        (hitTip.hitRate * 100).toFixed(1)
      }}%），提示词前缀抖动可能偏大，固定 system prompt 前缀可提升命中。
      <button type="button" class="ml-2 text-accent hover:underline" @click="dismissHitTip">
        关闭
      </button>
    </div>

    <div v-if="bothFailed" class="py-8 text-center">
      <HEmpty title="用量加载失败" description="汇总与明细均加载失败，请检查本地库后重试。" />
      <div class="mt-2">
        <HButton variant="secondary" size="sm" @click="runQuery">重试</HButton>
      </div>
    </div>
    <template v-else>
      <div class="relative">
        <div
          v-if="degraded"
          class="pointer-events-none absolute inset-0 z-10 flex items-center justify-center"
          aria-hidden="true"
        >
          <span class="rounded bg-bg px-2 py-1 text-xs text-fg-subtle opacity-80"
            >本地库不可用</span
          >
        </div>
        <div :class="degraded ? 'opacity-50 grayscale' : ''" class="flex flex-col gap-3">
          <section aria-label="用量趋势">
            <h3 class="mb-1 text-xs font-medium text-fg-muted">趋势</h3>
            <div class="relative">
              <div ref="trendEl" style="height: 320px" role="img" aria-label="用量趋势图" />
              <div
                v-if="summary && !summaryFailed && byDayEmpty"
                class="absolute inset-0 flex items-center justify-center"
              >
                <HEmpty title="暂无数据" description="当前筛选条件下没有按日用量。" />
              </div>
            </div>
          </section>
          <section aria-label="模型分布">
            <h3 class="mb-1 text-xs font-medium text-fg-muted">分布</h3>
            <div class="relative">
              <div ref="pieEl" style="height: 260px" role="img" aria-label="模型分布饼图" />
              <div
                v-if="summary && !summaryFailed && byModelEmpty"
                class="absolute inset-0 flex items-center justify-center"
              >
                <HEmpty title="暂无数据" description="当前筛选条件下没有模型用量。" />
              </div>
            </div>
          </section>
        </div>
      </div>

      <DetailTable
        :rows="rows"
        :total="total"
        :page="page"
        :page-size="pageSize"
        :page-size-options="PAGE_SIZE_OPTIONS"
        :loading="querying"
        :failed="queryFailed"
        :mask-on="maskOn"
        @update:page="onPage"
        @update:page-size="onPageSize"
      />
    </template>

    <section aria-label="数据治理" class="rounded-md border border-border-subtle p-3">
      <div class="mb-2 flex items-center justify-between gap-2">
        <h3 class="text-sm font-medium text-fg">数据治理</h3>
        <span
          class="rounded-full border border-border-subtle bg-bg-elevated px-2 py-0.5 text-[11px] text-fg-muted"
        >
          库大小 {{ stats ? formatBytes(stats.sizeBytes) : '—' }} ·
          {{ stats ? formatZhCount(stats.rowCount) : '—' }}条
        </span>
      </div>
      <div class="gov-grid">
        <div class="rounded-md border border-border-subtle bg-bg-elevated p-2.5">
          <div class="text-xs font-medium text-fg">存储与清理</div>
          <div class="mb-2 text-[11px] text-fg-muted">删除指定时间前数据，不可恢复</div>
          <div class="flex flex-wrap items-center gap-2">
            <HButton variant="secondary" size="sm" @click="onClear90">清理 90 天前数据</HButton>
          </div>
        </div>
        <div class="rounded-md border border-border-subtle bg-bg-elevated p-2.5">
          <div class="text-xs font-medium text-fg">配额告警</div>
          <div class="mb-2 text-[11px] text-fg-muted">本月回合超阈值时提示一次/会话</div>
          <div class="flex flex-wrap items-center gap-2">
            <label class="flex items-center gap-1 text-xs text-fg-muted">
              阈值
              <HInput v-model="monthlyLimitInput" size="sm" placeholder="空=关闭" class="w-28" />
            </label>
            <HButton variant="ghost" size="sm" @click="saveMonthlyLimit">保存阈值</HButton>
          </div>
        </div>
        <div class="rounded-md border border-border-subtle bg-bg-elevated p-2.5">
          <div class="text-xs font-medium text-fg">缓存命中提示</div>
          <div class="mb-2 text-[11px] text-fg-muted">命中率低时横幅提示</div>
          <div class="flex flex-wrap items-center gap-2 text-xs text-fg-muted">
            <label class="flex items-center gap-1">
              ≥
              <HInput
                :model-value="hitTipMinReq"
                size="sm"
                type="number"
                class="w-20"
                @update:model-value="hitTipMinReq = Number($event)"
              />
              次且命中率 &lt;
              <HInput
                :model-value="hitTipMinRate"
                size="sm"
                type="number"
                class="w-20"
                @update:model-value="hitTipMinRate = Number($event)"
              />
            </label>
            <HButton variant="ghost" size="sm" @click="saveHitTipThresholds">保存</HButton>
          </div>
        </div>
      </div>
      <div
        v-if="stats && stats.rowCount > 0"
        class="mt-2 rounded-md border border-danger/30 bg-danger-subtle/30 p-2.5"
      >
        <div class="text-xs font-medium text-danger">危险操作 · 全部清空</div>
        <div class="mt-1 flex items-center gap-2">
          <HButton variant="danger" size="sm" @click="onFullClear">全部清空…</HButton>
        </div>
      </div>
    </section>

    <HDialog
      :open="showClearDialog"
      :title="showFullClear ? '全部清空用量数据？' : '清理用量数据？'"
      width="sm"
      @update:open="showClearDialog = $event"
    >
      <p v-if="!showFullClear" class="text-sm">将删除 90 天前的数据，该操作不可恢复。</p>
      <div v-if="showFullClear" class="mt-2 flex flex-col gap-2">
        <p class="text-xs text-danger">危险操作：请输入 CLEAR 确认清空全部数据。</p>
        <HInput v-model="clearConfirmText" size="sm" placeholder="CLEAR" />
      </div>
      <template #footer>
        <HButton variant="secondary" size="sm" @click="showClearDialog = false">取消</HButton>
        <HButton v-if="showFullClear" variant="danger" size="sm" @click="confirmFullClear"
          >确认全部清空</HButton
        >
        <HButton v-else variant="danger" size="sm" @click="confirmClear90">清理 90 天前</HButton>
      </template>
    </HDialog>
  </div>
</template>

<style scoped>
.gov-grid {
  display: grid;
  grid-template-columns: repeat(3, minmax(0, 1fr));
  gap: 0.5rem;
}
@media (max-width: 900px) {
  .gov-grid {
    grid-template-columns: 1fr;
  }
}
</style>

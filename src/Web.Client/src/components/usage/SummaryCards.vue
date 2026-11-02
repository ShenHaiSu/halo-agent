<!-- src/Web.Client/src/components/usage/SummaryCards.vue -->
<!-- P17 用量汇总卡：第一行 总Token / 命中率 / 回合数（一行=一回合），第二行 未命中输入 / 命中缓存 / 输出。值中文紧凑 + title 精确。 -->
<script setup lang="ts">
import { formatNumber, formatZhCount } from '@/utils/format';
import { missOf } from './charts/useUsageCharts';
import { formatHitRate, type UsageSummary } from './usageTypes';

defineProps<{
  summary: UsageSummary | null;
  failed: boolean;
}>();

interface Card {
  label: string;
  value: string;
  title: string;
}

/** 总Token = 未命中输入 + 命中缓存 + 输出（与第二行三卡之和一致，脏数据截零口径与 missOf 对齐）。 */
function totalOf(s: UsageSummary): number {
  const miss = missOf(s.input, s.cached);
  return miss + s.cached + s.output;
}

function topCards(s: UsageSummary): Card[] {
  const total = totalOf(s);
  return [
    { label: '总 Token', value: formatZhCount(total), title: formatNumber(total) },
    {
      label: '命中率',
      value: formatHitRate(s.input, s.hitRate),
      title: s.input > 0 ? `${s.hitRate * 100}%` : '—',
    },
    { label: '回合数', value: formatZhCount(s.requests), title: formatNumber(s.requests) },
  ];
}

function bottomCards(s: UsageSummary): Card[] {
  const miss = missOf(s.input, s.cached);
  return [
    { label: '未命中输入', value: formatZhCount(miss), title: formatNumber(miss) },
    { label: '命中缓存', value: formatZhCount(s.cached), title: formatNumber(s.cached) },
    { label: '输出', value: formatZhCount(s.output), title: formatNumber(s.output) },
  ];
}

const TOP_LABELS = ['总 Token', '命中率', '回合数'];
const BOTTOM_LABELS = ['未命中输入', '命中缓存', '输出'];
</script>

<template>
  <section aria-label="用量汇总" class="flex flex-col gap-2">
    <template v-if="summary && !failed">
      <div class="grid grid-cols-3 gap-2">
        <div
          v-for="c in topCards(summary)"
          :key="c.label"
          class="rounded-md border border-border-subtle bg-bg-elevated px-3 py-2"
        >
          <div class="text-xs text-fg-muted">{{ c.label }}</div>
          <div class="text-lg font-semibold text-fg" :title="c.title">{{ c.value }}</div>
        </div>
      </div>
      <div class="grid grid-cols-3 gap-2">
        <div
          v-for="c in bottomCards(summary)"
          :key="c.label"
          class="rounded-md border border-border-subtle bg-bg-elevated px-3 py-2"
        >
          <div class="text-xs text-fg-muted">{{ c.label }}</div>
          <div class="text-lg font-semibold text-fg" :title="c.title">{{ c.value }}</div>
        </div>
      </div>
    </template>
    <template v-else>
      <div class="grid grid-cols-3 gap-2">
        <div
          v-for="label in TOP_LABELS"
          :key="label"
          class="rounded-md border border-border-subtle bg-bg-elevated px-3 py-2"
        >
          <div class="text-xs text-fg-muted">{{ label }}</div>
          <div class="text-lg font-semibold text-fg-subtle">—</div>
        </div>
      </div>
      <div class="grid grid-cols-3 gap-2">
        <div
          v-for="label in BOTTOM_LABELS"
          :key="label"
          class="rounded-md border border-border-subtle bg-bg-elevated px-3 py-2"
        >
          <div class="text-xs text-fg-muted">{{ label }}</div>
          <div class="text-lg font-semibold text-fg-subtle">—</div>
        </div>
      </div>
    </template>
  </section>
</template>

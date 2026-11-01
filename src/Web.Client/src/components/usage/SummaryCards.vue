<!-- src/Web.Client/src/components/usage/SummaryCards.vue -->
<!-- P17 用量汇总卡：未命中输入 / 命中缓存 / 输出 / 命中率 / 请求数。值中文紧凑 + title 精确。 -->
<script setup lang="ts">
import { formatNumber, formatZhCount } from '@/utils/format';
import { missOf } from './charts/useUsageCharts';
import { formatHitRate, type UsageSummary } from './usageTypes';

defineProps<{
  summary: UsageSummary | null;
  failed: boolean;
}>();

const cards = (s: UsageSummary): Array<{ label: string; value: string; title: string }> => {
  const miss = missOf(s.input, s.cached);
  return [
    { label: '未命中输入', value: formatZhCount(miss), title: formatNumber(miss) },
    { label: '命中缓存', value: formatZhCount(s.cached), title: formatNumber(s.cached) },
    { label: '输出', value: formatZhCount(s.output), title: formatNumber(s.output) },
    // 命中率卡片显示真实值（可 >100%，暴露脏数据）；折线另做可视限幅。
    {
      label: '命中率',
      value: formatHitRate(s.input, s.hitRate),
      title: s.input > 0 ? `${s.hitRate * 100}%` : '—',
    },
    { label: '请求数', value: formatZhCount(s.requests), title: formatNumber(s.requests) },
  ];
};
</script>

<template>
  <section aria-label="用量汇总" class="grid grid-cols-2 gap-2 md:grid-cols-5">
    <template v-if="summary && !failed">
      <div
        v-for="c in cards(summary)"
        :key="c.label"
        class="rounded-md border border-border-subtle bg-bg-elevated px-3 py-2"
      >
        <div class="text-xs text-fg-muted">{{ c.label }}</div>
        <div class="text-lg font-semibold text-fg" :title="c.title">{{ c.value }}</div>
      </div>
    </template>
    <template v-else>
      <div
        v-for="label in ['未命中输入', '命中缓存', '输出', '命中率', '请求数']"
        :key="label"
        class="rounded-md border border-border-subtle bg-bg-elevated px-3 py-2"
      >
        <div class="text-xs text-fg-muted">{{ label }}</div>
        <div class="text-lg font-semibold text-fg-subtle">—</div>
      </div>
    </template>
  </section>
</template>

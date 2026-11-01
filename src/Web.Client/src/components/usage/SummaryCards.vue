<!-- src/Web.Client/src/components/usage/SummaryCards.vue -->
<!-- P16 用量汇总卡：输入 / 命中缓存 / 输出 / 命中率 / 请求数。 -->
<script setup lang="ts">
import { formatNumber } from '@/utils/format';
import { formatHitRate, type UsageSummary } from './usageTypes';

defineProps<{
  summary: UsageSummary | null;
  failed: boolean;
}>();

const cards = (s: UsageSummary): Array<{ label: string; value: string }> => [
  { label: '输入', value: formatNumber(s.input) },
  { label: '命中缓存', value: formatNumber(s.cached) },
  { label: '输出', value: formatNumber(s.output) },
  { label: '命中率', value: formatHitRate(s.input, s.hitRate) },
  { label: '请求数', value: formatNumber(s.requests) },
];
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
        <div class="text-lg font-semibold text-fg">{{ c.value }}</div>
      </div>
    </template>
    <template v-else>
      <div
        v-for="label in ['输入', '命中缓存', '输出', '命中率', '请求数']"
        :key="label"
        class="rounded-md border border-border-subtle bg-bg-elevated px-3 py-2"
      >
        <div class="text-xs text-fg-muted">{{ label }}</div>
        <div class="text-lg font-semibold text-fg-subtle">—</div>
      </div>
    </template>
  </section>
</template>

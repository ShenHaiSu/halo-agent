<!-- src/Web.Client/src/components/usage/DetailTable.vue -->
<!-- P16 用量明细表：分页 + 会话下钻列。 -->
<script setup lang="ts">
import HButton from '@/components/base/HButton.vue';
import HEmpty from '@/components/base/HEmpty.vue';
import { formatDateTime, formatNumber } from '@/utils/format';
import { formatHitRate, type UsageRow } from './usageTypes';

defineProps<{
  rows: UsageRow[];
  total: number;
  page: number;
  pageSize: number;
  loading: boolean;
  failed: boolean;
}>();

const emit = defineEmits<{
  'update:page': [number];
  drilldown: [string];
}>();

function totalPages(total: number, pageSize: number): number {
  return Math.max(1, Math.ceil(total / pageSize));
}
</script>

<template>
  <section aria-label="用量明细">
    <div v-if="failed" class="py-6">
      <HEmpty title="明细加载失败" description="查询接口异常，稍后重试。" />
    </div>
    <div v-else-if="!loading && rows.length === 0" class="py-6">
      <HEmpty title="暂无数据" description="当前筛选条件下没有用量记录。" />
    </div>
    <div v-else class="overflow-x-auto">
      <table class="w-full text-sm">
        <thead>
          <tr class="border-b border-border-subtle text-left text-xs text-fg-muted">
            <th class="px-2 py-1.5 font-medium">时间</th>
            <th class="px-2 py-1.5 font-medium">服务商</th>
            <th class="px-2 py-1.5 font-medium">模型</th>
            <th class="px-2 py-1.5 font-medium">会话</th>
            <th class="px-2 py-1.5 text-right font-medium">输入</th>
            <th class="px-2 py-1.5 text-right font-medium">缓存</th>
            <th class="px-2 py-1.5 text-right font-medium">输出</th>
            <th class="px-2 py-1.5 text-right font-medium">合计</th>
            <th class="px-2 py-1.5 text-right font-medium">命中率</th>
            <th class="px-2 py-1.5 font-medium">截断</th>
          </tr>
        </thead>
        <tbody>
          <tr v-for="r in rows" :key="`${r.sessionId}/${r.turnId}`" class="border-b border-border-subtle">
            <td class="whitespace-nowrap px-2 py-1.5 text-fg-muted">{{ formatDateTime(r.tsMs) }}</td>
            <td class="px-2 py-1.5" :title="r.providerDisplay || r.providerId">{{ r.providerId }}</td>
            <td class="px-2 py-1.5" :title="r.modelDisplay || r.modelId">{{ r.modelId }}</td>
            <td class="max-w-40 truncate px-2 py-1.5">
              <button
                type="button"
                class="text-accent hover:underline focus-visible:outline-none focus-visible:ring-2 focus-visible:ring-accent/40"
                :title="r.sessionId"
                @click="emit('drilldown', r.sessionId)"
              >
                {{ r.sessionId }}
              </button>
            </td>
            <td class="px-2 py-1.5 text-right">{{ formatNumber(r.prompt) }}</td>
            <td class="px-2 py-1.5 text-right">{{ formatNumber(r.cached) }}</td>
            <td class="px-2 py-1.5 text-right">{{ formatNumber(r.output) }}</td>
            <td class="px-2 py-1.5 text-right">{{ formatNumber(r.total) }}</td>
            <td class="px-2 py-1.5 text-right">{{ formatHitRate(r.prompt, r.hitRate) }}</td>
            <td class="px-2 py-1.5 text-fg-muted">{{ r.truncated === 1 ? '是' : '—' }}</td>
          </tr>
        </tbody>
      </table>
    </div>
    <div v-if="!failed && total > 0" class="mt-2 flex items-center justify-between text-xs text-fg-muted">
      <span>共 {{ formatNumber(total) }} 条</span>
      <div class="flex items-center gap-2">
        <HButton
          variant="ghost"
          size="sm"
          :disabled="page <= 1 || loading"
          @click="emit('update:page', page - 1)"
        >
          上一页
        </HButton>
        <span>{{ page }} / {{ totalPages(total, pageSize) }}</span>
        <HButton
          variant="ghost"
          size="sm"
          :disabled="page >= totalPages(total, pageSize) || loading"
          @click="emit('update:page', page + 1)"
        >
          下一页
        </HButton>
      </div>
    </div>
  </section>
</template>

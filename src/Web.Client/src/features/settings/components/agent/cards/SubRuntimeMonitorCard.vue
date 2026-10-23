<template>
  <n-card title="子运行监控" size="small">
    <div class="flex items-center gap-2 flex-wrap mb-2">
      <n-button size="small" data-testid="agent-sub-refresh" :loading="loading" @click="emit('refresh')"> 刷新 </n-button>
      <n-button size="small" type="error" ghost data-testid="agent-sub-cancel-tree" :disabled="!rows.length" @click="emit('cancelTree')">
        掐断整树
      </n-button>
      <span v-if="policyText" class="text-xs text-secondary">{{ policyText }}</span>
    </div>
    <n-alert v-if="rejection" type="warning" :show-icon="true" class="mb-2" data-testid="agent-sub-rejection">
      最近拒绝 [{{ rejection.code }}] {{ rejection.callId }} · {{ formatTime(rejection.atUtc) }}
    </n-alert>
    <div class="overflow-x-auto">
      <n-data-table :columns="columns" :data="rows" :bordered="false" size="small" data-testid="agent-runtime-table" />
    </div>
    <n-empty v-if="!rows.length && !loading" description="当前无子代理运行" class="mt-2" />
  </n-card>
</template>
<script setup lang="ts">
/**
 * need23：子运行监控卡（此前未在父视图 import，导致整卡不渲染）。
 * 列定义收归本卡：轮次/工具数/最近工具均为后端真实统计，"历史"按行可点并传完整 subId。
 */
import { computed, h } from 'vue';
import { NButton } from 'naive-ui';
import type { SubRejectionRow, SubRuntimeRow } from './subRuntimeTypes';

defineProps<{
  rows: SubRuntimeRow[];
  loading: boolean;
  policyText: string;
  rejection: SubRejectionRow | null;
}>();
const emit = defineEmits<{
  (e: 'refresh'): void;
  (e: 'cancelTree'): void;
  (e: 'openHistory', subId: string): void;
}>();

function formatTime(iso: string): string {
  if (!iso) return '';
  const d = new Date(iso);
  if (Number.isNaN(d.getTime())) return iso;
  return d.toLocaleString();
}

const columns = computed(() => [
  { title: 'ID', key: 'shortId', width: 84 },
  { title: 'label', key: 'label', width: 130, ellipsis: true },
  { title: '状态', key: 'status', width: 80 },
  { title: '轮次', key: 'iteration', width: 60 },
  { title: '工具数', key: 'toolCount', width: 70 },
  { title: '最近工具', key: 'lastTool', width: 130, ellipsis: true },
  { title: '耗时ms', key: 'elapsedMs', width: 90 },
  {
    title: '操作',
    key: 'actions',
    width: 92,
    render: (row: SubRuntimeRow) =>
      h(
        NButton,
        {
          size: 'tiny',
          quaternary: true,
          type: 'primary',
          'data-testid': 'agent-sub-open-history',
          onClick: () => emit('openHistory', row.subId),
        },
        { default: () => '历史' }
      ),
  },
]);
</script>

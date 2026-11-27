<template>
  <div class="agent-monitor-body">
    <div class="agent-monitor-toolbar">
      <n-button size="small" data-testid="agent-sub-refresh" :loading="loading" @click="emit('refresh')">刷新</n-button>
      <n-button size="small" type="error" ghost data-testid="agent-sub-cancel-tree" :disabled="!rows.length" @click="emit('cancelTree')">
        掐断整树
      </n-button>
      <n-tag v-if="policyText" size="small" :bordered="false" class="agent-policy-tag" :title="policyText">
        {{ policyText }}
      </n-tag>
    </div>
    <n-alert v-if="rejection" type="warning" :show-icon="true" class="agent-monitor-alert" data-testid="agent-sub-rejection">
      <template #default>
        <div>最近拒绝 [{{ rejection.code }}] {{ rejection.callId }} · {{ formatRejectionTime(rejection.atUtc) }}</div>
        <n-button size="tiny" quaternary data-testid="agent-sub-view-rejection" @click="emit('viewRejection')">查看记录</n-button>
      </template>
    </n-alert>
    <div class="agent-monitor-table-wrap" role="region" aria-label="子代理运行监控表">
      <n-data-table :columns="columns" :data="rows" :bordered="false" size="small" data-testid="agent-runtime-table" />
    </div>
    <n-empty v-if="!rows.length && !loading" description="当前无子代理运行" class="agent-monitor-empty" />
  </div>
</template>
<script setup lang="ts">
import { computed, h } from 'vue';
import { NButton } from 'naive-ui';
import type { SubRejectionRow, SubRuntimeRow } from './subRuntimeTypes';
import { formatRejectionTime } from '../utils/agentBehaviorFormat';

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
  (e: 'viewRejection'): void;
}>();

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
          'aria-label': `进入子会话 ${row.shortId}`,
          onClick: () => emit('openHistory', row.subId),
        },
        { default: () => '历史' }
      ),
  },
]);
</script>

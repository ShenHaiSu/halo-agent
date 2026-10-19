<template>
  <n-card title="子运行监控" size="small">
    <div class="flex items-center gap-2 flex-wrap mb-2">
      <n-button size="small" data-testid="agent-sub-refresh" :loading="loading" @click="emit('refresh')">刷新</n-button>
      <n-button size="small" type="error" ghost data-testid="agent-sub-cancel-tree" @click="emit('cancelTree')">掐断整树</n-button>
      <n-button size="small" ghost data-testid="sub-runtime-open-history" @click="emit('openHistory', String((rows[0] as Record<string, unknown>)?.subId || ''))">历史</n-button>
      <span v-if="policyText" class="text-xs text-secondary">{{ policyText }}</span>
    </div>
    <n-alert v-if="rejection" type="warning" :show-icon="true" class="mb-2">最近拒绝 [{{ rejection.code }}] {{ rejection.callId }}</n-alert>
    <div class="overflow-x-auto">
      <n-data-table :columns="columns" :data="rows" :bordered="false" size="small" data-testid="agent-runtime-table" />
    </div>
    <n-empty v-if="!rows.length && !loading" description="当前无子代理运行" class="mt-2" />
  </n-card>
</template>
<script setup lang="ts">
defineProps<{ rows: Array<Record<string, unknown>>; columns: Array<Record<string, unknown>>; loading: boolean; policyText: string; rejection: { code: string; callId: string } | null }>();
const emit = defineEmits<{ (e: 'refresh'): void; (e: 'cancelTree'): void; (e: 'openHistory', subId: string): void }>();
</script>

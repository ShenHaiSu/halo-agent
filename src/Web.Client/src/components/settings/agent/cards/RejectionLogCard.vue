<template>
  <n-card title="拒绝与自愈记录" size="small">
    <div v-if="!rows.length" class="text-xs text-secondary">暂无拒绝记录</div>
    <div v-else class="space-y-1">
      <div v-for="(r,i) in rows" :key="i" data-testid="agent-rejection-row" class="text-xs font-mono">[{{ r.code }}] {{ r.callId }} · {{ r.atUtc }}</div>
    </div>
  </n-card>
</template>
<script setup lang="ts">
defineProps<{ rows: Array<{ code: string; callId: string; atUtc: string }> }>();
</script>

<template>
  <div class="agent-rejection-body">
    <div v-if="!rows.length" class="agent-field-hint">暂无拒绝记录</div>
    <div v-else class="agent-rejection-list">
      <div v-for="(r, i) in rows" :key="`${r.code}-${r.callId}-${i}`" data-testid="agent-rejection-row" class="agent-rejection-row">
        [{{ r.code }}] {{ r.callId }} · {{ formatRejectionTime(r.atUtc) }}
      </div>
    </div>
  </div>
</template>
<script setup lang="ts">
import { formatRejectionTime } from '../utils/agentBehaviorFormat';

defineProps<{ rows: Array<{ code: string; callId: string; atUtc: string }> }>();
</script>

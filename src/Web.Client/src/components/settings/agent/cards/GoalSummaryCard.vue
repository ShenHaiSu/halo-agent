<template>
  <n-card title="目标摘要" size="small" data-testid="agent-goal-summary">
    <div v-if="!goal || (!goal.title && !goal.description && !(goal.milestones||[]).length)" class="text-xs text-secondary">尚未设定目标</div>
    <div v-else class="text-xs space-y-1">
      <div class="font-medium">{{ goal.title }}</div>
      <div class="text-secondary truncate">{{ goal.description }}</div>
      <n-progress type="line" :percentage="goal.percent || 0" :show-indicator="false" />
    </div>
    <n-button size="tiny" class="mt-2" data-testid="agent-goal-edit-jump" @click="emit('jump')">去浮动窗编辑</n-button>
  </n-card>
</template>
<script setup lang="ts">
import type { SessionGoalDto } from '@/bridge/hostBridge';
defineProps<{ goal: SessionGoalDto | null }>();
const emit = defineEmits<{ (e: 'jump'): void }>();
</script>

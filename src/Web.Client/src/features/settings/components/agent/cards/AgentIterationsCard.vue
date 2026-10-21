<template>
  <n-card title="工具轮次" size="small">
    <div class="grid grid-cols-[96px_1fr_auto] items-center gap-2">
      <label class="text-[13px] font-medium">最大轮次</label>
      <n-input-number :value="cfg.maxIterations" :min="0" :max="500" size="small" class="w-44 font-mono" data-testid="agent-iterations-input" @update:value="(v:number|null)=>emit('update','maxIterations',v)" />
      <n-button size="tiny" data-testid="agent-reset-iterations" @click="emit('reset','iterations')">恢复本项默认</n-button>
    </div>
  </n-card>
</template>
<script setup lang="ts">
defineProps<{ cfg: { maxIterations: number } }>();
const emit = defineEmits<{ (e: 'update', f: string, v: number | null): void; (e: 'reset', f: string): void }>();
</script>

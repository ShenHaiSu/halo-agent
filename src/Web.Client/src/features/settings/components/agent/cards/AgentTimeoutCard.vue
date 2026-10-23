<template>
  <n-card title="命令执行超时" size="small">
    <div class="grid grid-cols-[96px_1fr_auto] items-center gap-2">
      <label class="text-[13px] font-medium">默认超时</label>
      <n-input-number
        :value="cfg.execDefaultTimeoutMs"
        :min="0"
        :max="3600000"
        :step="5000"
        :show-button="false"
        size="small"
        class="w-44 font-mono"
        data-testid="agent-timeout-default-input"
        @update:value="(v: number | null) => emit('update', 'execDefaultTimeoutMs', v)"
        ><template #suffix>ms</template></n-input-number
      >
      <n-button size="tiny" data-testid="agent-reset-defaultTimeout" @click="emit('reset', 'defaultTimeout')">恢复本项默认</n-button>
    </div>
    <div class="grid grid-cols-[96px_1fr_auto] items-center gap-2 mt-2">
      <label class="text-[13px] font-medium">上限</label>
      <n-input-number
        :value="cfg.execMaxTimeoutMs"
        :min="0"
        :max="3600000"
        :step="5000"
        :show-button="false"
        size="small"
        class="w-44 font-mono"
        data-testid="agent-timeout-max-input"
        @update:value="(v: number | null) => emit('update', 'execMaxTimeoutMs', v)"
        ><template #suffix>ms</template></n-input-number
      >
      <n-button size="tiny" data-testid="agent-reset-maxTimeout" @click="emit('reset', 'maxTimeout')">恢复本项默认</n-button>
    </div>
  </n-card>
</template>
<script setup lang="ts">
defineProps<{ cfg: { execDefaultTimeoutMs: number; execMaxTimeoutMs: number } }>();
const emit = defineEmits<{
  (e: 'update', f: 'execDefaultTimeoutMs' | 'execMaxTimeoutMs', v: number | null): void;
  (e: 'reset', f: 'defaultTimeout' | 'maxTimeout'): void;
}>();
</script>

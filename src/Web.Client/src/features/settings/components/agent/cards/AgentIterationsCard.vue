<template>
  <n-card title="工具轮次" size="small">
    <div class="grid grid-cols-[96px_1fr_auto] items-center gap-2">
      <label class="text-[13px] font-medium">最大轮次</label>
      <n-input-number
        :value="cfg.maxIterations"
        :min="0"
        :max="500"
        size="small"
        class="w-44 font-mono"
        data-testid="agent-iterations-input"
        @update:value="(v: number | null) => emit('update', 'maxIterations', v)"
      />
      <n-button size="tiny" data-testid="agent-reset-iterations" @click="emit('reset', 'iterations')">恢复本项默认</n-button>
    </div>
    <div class="grid grid-cols-[96px_1fr_auto] items-center gap-2 mt-2">
      <label class="text-[13px] font-medium">单轮最大时间</label>
      <n-input-number
        :value="cfg.turnTimeoutMs ?? 600000"
        :min="0"
        :max="600000"
        :step="10000"
        :show-button="false"
        :disabled="isInfinite"
        size="small"
        class="w-44 font-mono"
        data-testid="agent-turn-timeout-input"
        @update:value="(v: number | null) => emit('update', 'turnTimeoutMs', v)"
        ><template #suffix>ms</template>
      </n-input-number>
      <n-button size="tiny" data-testid="agent-reset-turn-timeout" @click="emit('reset', 'turnTimeout')">恢复本项默认</n-button>
    </div>
    <div class="flex items-center gap-2 flex-wrap mt-2">
      <n-switch :value="isInfinite" data-testid="agent-turn-timeout-infinite" @update:value="onInfinite" />
      <span class="text-xs text-secondary">无限时间（0=不限，关闭则恢复默认 10min/600s）</span>
      <span v-if="!isInfinite" class="text-xs font-mono text-secondary">约 {{ secondsText }}s / 最大 600s</span>
    </div>
    <p class="text-[11px] text-secondary mt-1">有限值范围 1000..600000ms，保存后下一轮对话起生效。</p>
  </n-card>
</template>
<script setup lang="ts">
import { computed } from 'vue';
const props = defineProps<{ cfg: { maxIterations: number; turnTimeoutMs?: number } }>();
const emit = defineEmits<{
  (e: 'update', f: 'maxIterations' | 'turnTimeoutMs', v: number | null): void;
  (e: 'reset', f: 'iterations' | 'turnTimeout'): void;
}>();
const isInfinite = computed(() => (props.cfg.turnTimeoutMs ?? 600000) === 0);
const secondsText = computed(() => ((props.cfg.turnTimeoutMs ?? 600000) / 1000).toFixed(0));
function onInfinite(v: boolean) {
  if (v) emit('update', 'turnTimeoutMs', 0);
  else emit('update', 'turnTimeoutMs', 600000);
}
</script>

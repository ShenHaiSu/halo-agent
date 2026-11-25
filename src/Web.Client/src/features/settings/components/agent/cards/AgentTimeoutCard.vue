<template>
  <div class="agent-behavior-fields">
    <div class="agent-setting-row" :class="{ 'is-changed': isDirty('execDefaultTimeoutMs') }">
      <label class="agent-field-label">默认超时</label>
      <n-input-number
        :value="cfg.execDefaultTimeoutMs"
        :min="0"
        :max="3600000"
        :step="5000"
        :show-button="false"
        size="small"
        class="w-44 font-mono"
        data-testid="agent-timeout-default-input"
        data-agent-field="execDefaultTimeoutMs"
        aria-label="默认超时（毫秒）"
        @update:value="(v: number | null) => emit('update', 'execDefaultTimeoutMs', v)"
      >
        <template #suffix>ms</template>
      </n-input-number>
      <n-button
        size="tiny"
        quaternary
        circle
        aria-label="恢复本项默认"
        title="恢复本项默认"
        data-testid="agent-reset-defaultTimeout"
        @click="emit('reset', 'defaultTimeout')"
      >
        <template #icon><RotateCcw :size="13" /></template>
      </n-button>
    </div>
    <div class="agent-setting-row" :class="{ 'is-changed': isDirty('execMaxTimeoutMs') }">
      <label class="agent-field-label">上限</label>
      <n-input-number
        :value="cfg.execMaxTimeoutMs"
        :min="0"
        :max="3600000"
        :step="5000"
        :show-button="false"
        size="small"
        class="w-44 font-mono"
        data-testid="agent-timeout-max-input"
        data-agent-field="execMaxTimeoutMs"
        aria-label="超时上限（毫秒）"
        @update:value="(v: number | null) => emit('update', 'execMaxTimeoutMs', v)"
      >
        <template #suffix>ms</template>
      </n-input-number>
      <n-button
        size="tiny"
        quaternary
        circle
        aria-label="恢复本项默认"
        title="恢复本项默认"
        data-testid="agent-reset-maxTimeout"
        @click="emit('reset', 'maxTimeout')"
      >
        <template #icon><RotateCcw :size="13" /></template>
      </n-button>
    </div>
    <p class="agent-field-hint">0 = 无限等待；上限 0 = 不限上限。</p>
  </div>
</template>
<script setup lang="ts">
import { RotateCcw } from 'lucide-vue-next';

const props = withDefaults(
  defineProps<{
    cfg: { execDefaultTimeoutMs: number; execMaxTimeoutMs: number };
    dirtyFields?: string[];
  }>(),
  { dirtyFields: () => [] }
);
const emit = defineEmits<{
  (e: 'update', f: 'execDefaultTimeoutMs' | 'execMaxTimeoutMs', v: number | null): void;
  (e: 'reset', f: 'defaultTimeout' | 'maxTimeout'): void;
}>();

function isDirty(field: string): boolean {
  return props.dirtyFields.includes(field);
}
</script>

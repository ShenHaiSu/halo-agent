<template>
  <div class="agent-behavior-fields">
    <div class="agent-setting-row" :class="{ 'is-changed': isDirty('maxIterations') }">
      <label class="agent-field-label">最大轮次</label>
      <n-input-number
        :value="cfg.maxIterations"
        :min="0"
        :max="500"
        size="small"
        class="w-44 font-mono"
        data-testid="agent-iterations-input"
        data-agent-field="maxIterations"
        aria-label="工具调用最大轮次"
        @update:value="(v: number | null) => emit('update', 'maxIterations', v)"
      />
      <n-button
        size="tiny"
        quaternary
        circle
        aria-label="恢复本项默认"
        title="恢复本项默认"
        data-testid="agent-reset-iterations"
        @click="emit('reset', 'iterations')"
      >
        <template #icon><RotateCcw :size="13" /></template>
      </n-button>
    </div>
    <div class="agent-setting-row" :class="{ 'is-changed': isDirty('turnTimeoutMs') }">
      <label class="agent-field-label">单轮最大时间</label>
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
        data-agent-field="turnTimeoutMs"
        aria-label="单轮最大时间（毫秒）"
        @update:value="(v: number | null) => emit('update', 'turnTimeoutMs', v)"
      >
        <template #suffix>ms</template>
      </n-input-number>
      <n-button
        size="tiny"
        quaternary
        circle
        aria-label="恢复本项默认"
        title="恢复本项默认"
        data-testid="agent-reset-turn-timeout"
        @click="emit('reset', 'turnTimeout')"
      >
        <template #icon><RotateCcw :size="13" /></template>
      </n-button>
    </div>
    <div class="agent-setting-row">
      <label class="agent-field-label">无限时间</label>
      <n-switch :value="isInfinite" data-testid="agent-turn-timeout-infinite" aria-label="开启无限时间" @update:value="onInfinite" />
      <AgentFieldHint text="开启后写入 0（不限时），关闭恢复默认 10 分钟 / 600000ms。" label="查看无限时间说明" />
    </div>
    <p class="agent-field-hint">有限范围 1000–600000ms；0 由开关写入。</p>
  </div>
</template>
<script setup lang="ts">
import { computed } from 'vue';
import { RotateCcw } from 'lucide-vue-next';
import AgentFieldHint from '../AgentFieldHint.vue';

const props = withDefaults(
  defineProps<{
    cfg: { maxIterations: number; turnTimeoutMs?: number };
    dirtyFields?: string[];
  }>(),
  { dirtyFields: () => [] }
);
const emit = defineEmits<{
  (e: 'update', f: 'maxIterations' | 'turnTimeoutMs', v: number | null): void;
  (e: 'reset', f: 'iterations' | 'turnTimeout'): void;
}>();

const isInfinite = computed(() => (props.cfg.turnTimeoutMs ?? 600000) === 0);

function onInfinite(v: boolean) {
  emit('update', 'turnTimeoutMs', v ? 0 : 600000);
}

function isDirty(field: string): boolean {
  return props.dirtyFields.includes(field);
}
</script>

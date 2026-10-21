<template>
  <div v-if="hasError" class="panel-boundary-error" data-testid="right-rail-panel-error">
    <p class="panel-boundary-title">该面板加载失败</p>
    <p class="panel-boundary-desc">{{ errorText }}</p>
    <n-button size="small" @click="retry">重试</n-button>
  </div>
  <slot v-else />
</template>
<script setup lang="ts">
/**
 * need19：面板级错误边界（自实现，防白屏）。
 * naive-ui@2.40.x 无 error-boundary 组件，故手写 onErrorCaptured 版本。
 */
import { ref, onErrorCaptured } from 'vue';

const hasError = ref(false);
const errorText = ref('');

onErrorCaptured((err: unknown) => {
  hasError.value = true;
  try {
    errorText.value = err instanceof Error ? err.message : String(err);
  } catch {
    errorText.value = '未知错误';
  }
  console.warn('[rightRail] panel error captured:', err);
  return false;
});

function retry(): void {
  hasError.value = false;
  errorText.value = '';
}
</script>
<style scoped>
.panel-boundary-error { padding: 24px 16px; text-align: center; color: var(--text-secondary); }
.panel-boundary-title { font-size: 13px; font-weight: 600; color: var(--text-primary); margin: 0 0 6px; }
.panel-boundary-desc { font-size: 12px; margin: 0 0 12px; word-break: break-all; }
</style>

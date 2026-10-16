<template>
  <div
    v-if="visible"
    data-testid="message-context-menu"
    class="history-context-menu"
    :style="{ left: `${x}px`, top: `${y}px` }"
    @click.stop
    @contextmenu.prevent
  >
    <button
      data-testid="context-menu-copy"
      class="history-context-menu-item"
      :disabled="!canCopy"
      :title="!canCopy ? '本轮无正文可复制' : '复制原文'"
      @click="emit('copy')"
    >
      <Copy class="w-3.5 h-3.5 shrink-0" />
      <span>复制</span>
    </button>
    <button
      data-testid="context-menu-replay"
      class="history-context-menu-item"
      :disabled="busy"
      :title="busy ? '生成中不可操作，请稍后再试' : '截断后重新生成'"
      @click="emit('replay')"
    >
      <RotateCcw class="w-3.5 h-3.5 shrink-0" />
      <span>重放</span>
    </button>
    <button
      data-testid="context-menu-branch"
      class="history-context-menu-item"
      :disabled="busy"
      :title="busy ? '生成中不可操作，请稍后再试' : '从这里创建分支会话'"
      @click="emit('branch')"
    >
      <GitBranch class="w-3.5 h-3.5 shrink-0" />
      <span>从这里分支</span>
    </button>
  </div>
</template>

<script setup lang="ts">
import { Copy, RotateCcw, GitBranch } from 'lucide-vue-next';

defineProps<{
  visible: boolean;
  x: number;
  y: number;
  busy: boolean;
  canCopy: boolean;
}>();

const emit = defineEmits<{
  (e: 'copy' | 'replay' | 'branch'): void;
}>();
</script>

<style scoped>
.history-context-menu {
  position: fixed;
  z-index: 50;
  width: 11rem;
  padding: 4px;
  border-radius: 8px;
  border: 1px solid var(--border-subtle, rgba(128, 128, 128, 0.25));
  background: var(--bg-surface, #1e1e1e);
  box-shadow: 0 8px 24px rgba(0, 0, 0, 0.35);
}
.history-context-menu-item {
  display: flex;
  align-items: center;
  gap: 8px;
  width: 100%;
  padding: 7px 10px;
  font-size: 12px;
  line-height: 1.4;
  border-radius: 6px;
  border: none;
  background: transparent;
  color: var(--text-primary, inherit);
  cursor: pointer;
  text-align: left;
  word-break: break-all;
}
.history-context-menu-item:hover:not(:disabled) {
  background: var(--bg-elevated, rgba(128, 128, 128, 0.18));
}
.history-context-menu-item:disabled {
  opacity: 0.4;
  cursor: not-allowed;
}
</style>

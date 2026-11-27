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
      :title="!canCopy ? '本条无可复制内容' : '仅复制本条原文'"
      @click="emit('copy')"
    >
      <Copy class="w-3.5 h-3.5 shrink-0" />
      <span>{{ copyLabel }}</span>
    </button>
    <button
      data-testid="context-menu-copy-turn"
      class="history-context-menu-item"
      :disabled="!canCopyTurn"
      :title="!canCopyTurn ? '本轮无可复制内容' : '复制整轮问答（含提问与回复，不含过程）'"
      @click="emit('copy-turn')"
    >
      <Copy class="w-3.5 h-3.5 shrink-0" />
      <span>复制整轮问答</span>
    </button>
    <button
      data-testid="context-menu-replay"
      class="history-context-menu-item"
      :disabled="busy || replayDisabled"
      :title="busy ? '忙碌中不可操作，请稍后再试' : replayTitle || '截断后重新生成'"
      @click="emit('replay')"
    >
      <RotateCcw class="w-3.5 h-3.5 shrink-0" />
      <span>重放</span>
    </button>
    <button
      data-testid="context-menu-branch"
      class="history-context-menu-item"
      :disabled="busy || branchDisabled"
      :title="busy ? '忙碌中不可操作，请稍后再试' : branchTitle || '从这里创建分支会话'"
      @click="emit('branch')"
    >
      <GitBranch class="w-3.5 h-3.5 shrink-0" />
      <span>从这里分支</span>
    </button>
    <button
      data-testid="context-menu-delete"
      class="history-context-menu-item history-context-menu-danger"
      :disabled="busy || deleteDisabled"
      :title="busy ? '生成中不可操作，请稍后再试' : deleteTitle || '删除该轮提问、回复及工具记录（先备份）'"
      @click="emit('delete')"
    >
      <Trash2 class="w-3.5 h-3.5 shrink-0" />
      <span>删除本轮</span>
    </button>
  </div>
</template>
<script setup lang="ts">
import { Copy, RotateCcw, GitBranch, Trash2 } from 'lucide-vue-next';

defineProps<{
  visible: boolean;
  x: number;
  y: number;
  busy: boolean;
  canCopy: boolean;
  canCopyTurn: boolean;
  copyLabel: string;
  replayDisabled?: boolean;
  branchDisabled?: boolean;
  replayTitle?: string;
  branchTitle?: string;
  deleteDisabled?: boolean;
  deleteTitle?: string;
}>();

const emit = defineEmits<{
  (e: 'copy' | 'copy-turn' | 'replay' | 'branch' | 'delete'): void;
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
.history-context-menu-danger {
  color: var(--text-danger, #f87171);
}
</style>

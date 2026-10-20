<template>
  <button
    ref="btnRef"
    class="right-rail-trigger"
    :class="{ 'is-open': ui.rightRailOpen }"
    data-testid="right-rail-trigger"
    :title="`历史目录与目标${pctText}`"
    aria-label="打开右侧栏：历史目录与目标"
    @click="onClick"
  >
    <component :is="ui.rightRailOpen ? PanelRightClose : PanelRightOpen" class="w-4 h-4" />
  </button>
</template>
<script setup lang="ts">
/**
 * need19：右侧竖向悬浮触发按钮（含 Goal 迷你徽标）。
 * 固定在 WorkbenchView 右侧中部（absolute，不用 fixed，避免盖住左侧栏/状态栏）。
 */
import { computed, ref } from 'vue';
import { PanelRightOpen, PanelRightClose } from 'lucide-vue-next';
import { useUiStore } from '@/stores/uiStore';
import { useGoalStore } from '@/stores/goalStore';

const ui = useUiStore();
const goalStore = useGoalStore();
const btnRef = ref<HTMLElement | null>(null);

const pct = computed(() => goalStore.pct || 0);
const hasGoal = computed(() => {
  const g = goalStore.goal;
  return !!g && (!!g.title || !!g.description || (g.milestones || []).length > 0);
});
const pctText = computed(() => (hasGoal.value ? `（${pct.value}%）` : '（未设定）'));

function onClick(): void {
  ui.toggle();
}

defineExpose({ focus: () => btnRef.value?.focus?.() });
</script>
<style scoped>
.right-rail-trigger {
  position: absolute;
  right: -8px;
  top: 50%;
  transform: translateY(-50%);
  z-index: 60;
  width: 32px;
  min-height: 44px;
  padding: 8px 0;
  display: flex;
  flex-direction: column;
  align-items: center;
  justify-content: center;
  gap: 4px;
  border-radius: 8px 0 0 8px;
  border: 1px solid var(--border-subtle);
  border-right: none;
  background: var(--bg-elevated);
  color: var(--text-secondary);
  cursor: pointer;
  opacity: 0.85;
  transition:
    opacity 0.12s,
    color 0.12s,
    background-color 0.12s;
}
.right-rail-trigger:hover {
  opacity: 1;
  color: var(--text-primary);
}
.right-rail-trigger.is-open {
  color: var(--accent-primary);
  border-color: var(--accent-primary);
}
</style>

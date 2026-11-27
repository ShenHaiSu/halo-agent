<template>
  <div class="agent-action-bar" role="toolbar" aria-label="智能体行为操作" data-testid="agent-action-bar">
    <n-tag :type="dirty ? 'warning' : 'success'" :bordered="false" aria-live="polite">
      {{ dirty ? `尚未保存（${dirtyCount} 项）` : '已保存' }}
    </n-tag>

    <div class="agent-action-buttons">
      <n-button size="small" data-testid="agent-collapse-all" @click="emit('toggle-all')">
        {{ allOpen ? '全部收起' : '全部展开' }}
      </n-button>
      <n-button v-if="dirty" size="small" data-testid="agent-discard" :disabled="saving" @click="emit('discard')"> 放弃更改 </n-button>
      <n-button size="small" data-testid="agent-reset-all" :disabled="saving" @click="emit('reset-all')"> 恢复全部默认 </n-button>
      <n-button
        type="primary"
        size="small"
        title="生效范围：全局 config/agent.json，保存后下一轮对话起生效"
        data-testid="agent-save"
        :loading="saving"
        :disabled="!dirty || saving || !canSave"
        @click="emit('save')"
      >
        保存
      </n-button>
    </div>
  </div>
</template>

<script setup lang="ts">
withDefaults(
  defineProps<{
    dirty: boolean;
    dirtyCount: number;
    saving: boolean;
    allOpen: boolean;
    canSave?: boolean;
  }>(),
  // 可选 Boolean prop 缺省为 false；这里默认允许保存，避免误把按钮永久禁用。
  { canSave: true }
);

const emit = defineEmits<{
  (e: 'save'): void;
  (e: 'discard'): void;
  (e: 'reset-all'): void;
  (e: 'toggle-all'): void;
}>();
</script>

<!-- src/Web.Client/src/components/detail/DetailTabs.vue -->
<!-- 详情栏五页：用量 / 工具 / 撤销 / 上下文 / MCP（HTabs 仅头，面板手写）。 -->
<script setup lang="ts">
import { ref } from 'vue';
import HTabs from '@/components/base/HTabs.vue';
import UsagePane from './UsagePane.vue';
import ToolsPane from './ToolsPane.vue';
import UndoPane from './UndoPane.vue';
import ContextPane from './ContextPane.vue';
import McpPane from './McpPane.vue';

const tab = ref('usage');

const tabs = [
  { key: 'usage', label: '用量' },
  { key: 'tools', label: '工具' },
  { key: 'undo', label: '撤销' },
  { key: 'context', label: '上下文' },
  { key: 'mcp', label: 'MCP' },
];
</script>

<template>
  <div class="detail-tabs">
    <HTabs v-model="tab" :items="tabs" variant="underline" />
    <div class="detail-body" role="tabpanel">
      <UsagePane v-if="tab === 'usage'" />
      <ToolsPane v-else-if="tab === 'tools'" />
      <UndoPane v-else-if="tab === 'undo'" />
      <ContextPane v-else-if="tab === 'context'" />
      <McpPane v-else />
    </div>
  </div>
</template>

<style scoped>
.detail-tabs {
  display: flex;
  flex-direction: column;
  height: 100%;
  min-height: 0;
}
.detail-body {
  flex: 1;
  min-height: 0;
  display: flex;
  flex-direction: column;
  overflow: hidden;
}
</style>

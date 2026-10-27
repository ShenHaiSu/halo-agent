<!-- src/Web.Client/src/components/detail/ToolsPane.vue -->
<!-- 工具分页：当前活跃工具列表（activeTools）。 -->
<script setup lang="ts">
import { computed } from 'vue';
import { useStreamStore } from '@/stores/stream';
import HEmpty from '@/components/base/HEmpty.vue';

const stream = useStreamStore();

const tools = computed(() => stream.activeToolList);

function argSummary(args: string): string {
  const one = args.replace(/\s+/g, ' ').trim();
  if (!one) return '（无参数）';
  return one.length <= 80 ? one : one.slice(0, 79) + '…';
}
</script>

<template>
  <div class="pane">
    <HEmpty
      v-if="tools.length === 0"
      icon="terminal"
      title="暂无工具调用"
      description="模型调用工具时，这里会显示正在执行的工具。"
    />
    <ul v-else class="tlist">
      <li v-for="t in tools" :key="t.toolCallId" class="titem">
        <span class="tname">{{ t.name }}</span>
        <span class="targs font-mono" :title="t.arguments">{{ argSummary(t.arguments) }}</span>
        <span v-if="t.alertLevel > 0" class="talert" role="status">
          {{ t.alertLevel >= 2 ? '可能卡住' : '运行较久' }}
        </span>
      </li>
    </ul>
  </div>
</template>

<style scoped>
.pane {
  padding: var(--space-3);
  overflow-y: auto;
  min-height: 0;
}
.tlist {
  list-style: none;
  margin: 0;
  padding: 0;
  display: flex;
  flex-direction: column;
  gap: var(--space-2);
}
.titem {
  display: flex;
  flex-direction: column;
  gap: 2px;
  padding: var(--space-2);
  border: 1px solid hsl(var(--border));
  border-radius: var(--radius-sm);
  background: hsl(var(--bg-elevated));
  min-width: 0;
}
.tname {
  font-size: var(--text-sm);
  font-weight: 500;
  color: hsl(var(--fg));
}
.targs {
  font-size: var(--text-xs);
  color: hsl(var(--fg-muted));
  overflow: hidden;
  text-overflow: ellipsis;
  white-space: nowrap;
}
.talert {
  font-size: var(--text-2xs);
  color: hsl(var(--warning));
}
</style>

<!-- src/Web.Client/src/components/chat/UnknownToolCard.vue -->
<!-- P11-D 去嵌套：降级为纯正文组件（头部/折叠/徽/时长唯一一份在 ToolCard）。
     props 保持 { tool }，prettyArgs + ToolOutput 逻辑原样搬运。 -->
<script setup lang="ts">
import { computed } from 'vue';
import type { ToolCallInfo } from '@/stores/messages';
import ToolOutput from './ToolOutput.vue';

const props = defineProps<{ tool: ToolCallInfo }>();

/** 参数：终版 args 美化 JSON（失败则原文），与 ToolCardBody 一致（首版不支持渐进 argsDelta）。 */
const prettyArgs = computed(() => {
  try {
    return JSON.stringify(JSON.parse(props.tool.arguments), null, 2);
  } catch {
    return props.tool.arguments;
  }
});
</script>

<template>
  <div class="unk-body">
    <section class="unk-section">
      <div class="unk-section-title">原生参数</div>
      <pre class="unk-pre">{{ prettyArgs }}</pre>
    </section>
    <section v-if="tool.output || tool.result" class="unk-section">
      <div class="unk-section-title">{{ tool.ok === false ? '错误' : '输出' }}</div>
      <ToolOutput :output="tool.output ?? tool.result ?? ''" />
    </section>
    <section v-else-if="tool.status === 'pending'" class="unk-pending">等待执行…</section>
  </div>
</template>

<style scoped>
.unk-body {
  border-top: 1px solid hsl(var(--border));
  padding: var(--space-2) var(--space-3);
  display: flex;
  flex-direction: column;
  gap: var(--space-3);
}
.unk-section-title {
  font-size: var(--text-2xs);
  color: hsl(var(--fg-subtle));
  text-transform: uppercase;
  letter-spacing: 0.04em;
  margin-bottom: var(--space-1);
}
.unk-pre {
  margin: 0;
  padding: var(--space-2);
  background: hsl(var(--bg-subtle));
  border-radius: var(--radius-sm);
  font-family: var(--font-mono);
  font-size: var(--text-xs);
  max-height: 260px;
  overflow: auto;
  white-space: pre-wrap;
  overflow-wrap: anywhere;
}
.unk-pending {
  color: hsl(var(--warning));
  font-size: var(--text-xs);
}
</style>

<!-- src/Web.Client/src/components/chat/ToolCardBody.vue -->
<!-- P5 D5-4 工具卡片正文：参数区（JSON 美化，失败则原文，超 2000 字符折叠）
     + 结果区（ToolOutput 纯文本输出 + ToolDiff 行级改动预览）。 -->
<script setup lang="ts">
import { computed, ref } from 'vue';
import type { ToolCallInfo } from '@/stores/messages';
import ToolOutput from './ToolOutput.vue';
import ToolDiff from './ToolDiff.vue';

const props = defineProps<{ tool: ToolCallInfo }>();

const PARAM_FOLD_AT = 2000;
const argsExpanded = ref(false);

/** 参数：尽量格式化 JSON（失败则原样） */
const prettyArgs = computed(() => {
  try {
    return JSON.stringify(JSON.parse(props.tool.arguments), null, 2);
  } catch {
    return props.tool.arguments;
  }
});

const argsTruncated = computed(() => prettyArgs.value.length > PARAM_FOLD_AT);
const shownArgs = computed(() =>
  argsTruncated.value && !argsExpanded.value
    ? prettyArgs.value.slice(0, PARAM_FOLD_AT) + '\n…'
    : prettyArgs.value
);

/** 实时输出（running）与最终结果（result） */
const liveOutput = computed(() =>
  props.tool.status === 'running' ? (props.tool.output ?? '') : ''
);
const result = computed(() => props.tool.result ?? '');
const isError = computed(() => props.tool.ok === false);
</script>

<template>
  <div class="tool-body">
    <section class="body-section">
      <div class="section-title">参数</div>
      <pre class="args-pre">{{ shownArgs }}</pre>
      <button
        v-if="argsTruncated && !argsExpanded"
        type="button"
        class="args-more"
        @click="argsExpanded = true"
      >
        展开全部参数（共 {{ prettyArgs.length }} 字符）
      </button>
    </section>

    <!-- 实时输出 -->
    <section v-if="liveOutput" class="body-section">
      <div class="section-title">输出</div>
      <ToolOutput :output="liveOutput" />
    </section>
    <section v-else-if="tool.status === 'running'" class="body-section pending-hint">
      正在执行，等待输出…
    </section>

    <!-- 最终结果：diff 预览 + 纯文本输出 -->
    <template v-if="result">
      <ToolDiff :output="result" />
      <section class="body-section">
        <div class="section-title">{{ isError ? '错误' : '结果' }}</div>
        <ToolOutput :output="result" />
      </section>
    </template>

    <section v-else-if="tool.status === 'pending'" class="body-section pending-hint">
      等待执行…
    </section>
  </div>
</template>

<style scoped>
.tool-body {
  border-top: 1px solid hsl(var(--border));
  padding: var(--space-2) var(--space-3);
  display: flex;
  flex-direction: column;
  gap: var(--space-3);
}
.section-title {
  font-size: var(--text-2xs);
  color: hsl(var(--fg-subtle));
  text-transform: uppercase;
  letter-spacing: 0.04em;
  margin-bottom: var(--space-1);
}
.args-pre {
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
.args-more {
  margin-top: var(--space-1);
  background: transparent;
  border: 0;
  padding: 0;
  color: hsl(var(--accent));
  font-size: var(--text-xs);
  cursor: pointer;
}
.args-more:hover {
  text-decoration: underline;
}
.pending-hint {
  color: hsl(var(--warning));
  font-size: var(--text-xs);
}
</style>

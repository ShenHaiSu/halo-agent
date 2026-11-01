<!-- src/Web.Client/src/components/chat/ToolCardBody.vue -->
<!-- P15-D8 半懒加载：首屏头部 2k + 展开才按需拉尾部；running 不走懒加载；失败回落头部。 -->
<script setup lang="ts">
import { computed, ref, watch } from 'vue';
import type { ToolCallInfo } from '@/stores/messages';
import { useMessagesStore } from '@/stores/messages';
import { transport } from '@/transport';
import { toolMeta } from './toolMeta';
import ToolOutput from './ToolOutput.vue';
import ToolDiff from './ToolDiff.vue';

const props = defineProps<{ tool: ToolCallInfo }>();
const messages = useMessagesStore();

const PARAM_FOLD_AT = 2000;

const prettyArgs = computed(() => {
  try {
    return JSON.stringify(JSON.parse(props.tool.arguments), null, 2);
  } catch {
    return props.tool.arguments;
  }
});

const argsTruncated = computed(() => prettyArgs.value.length > PARAM_FOLD_AT);
const shownArgs = computed(() => {
  const tail = messages.toolTail.get(props.tool.id)?.argsRest ?? '';
  const full = prettyArgs.value + tail;
  if (full.length > PARAM_FOLD_AT && !argsExpanded.value && props.tool.truncated) {
    return prettyArgs.value.slice(0, PARAM_FOLD_AT) + '\n…';
  }
  return full;
});
const argsExpanded = ref(false);

const liveOutput = computed(() =>
  props.tool.status === 'running' ? (props.tool.output ?? '') : ''
);
const tailOutput = computed(() => messages.toolTail.get(props.tool.id)?.outputRest ?? '');
const result = computed(() => (props.tool.result ?? '') + tailOutput.value);
const isError = computed(() => props.tool.ok === false);
const diffFirst = computed(() => toolMeta(props.tool.name, props.tool.kind).mutates);

const loadingTail = computed(() => messages.toolTail.get(props.tool.id)?.loading === true);
const tailError = computed(() => messages.toolTail.get(props.tool.id)?.error ?? null);

async function fetchTail(): Promise<void> {
  // N4：只懒已完成；running 看直播；Truncated=false 不请求
  if (props.tool.status === 'running') return;
  if (!props.tool.truncated) return;
  const cur = messages.toolTail.get(props.tool.id);
  if (cur?.outputRest !== undefined || cur?.loading) return;
  messages.toolTail.set(props.tool.id, { ...cur, loading: true, error: null });
  try {
    const r = await transport.request<{
      argsSlice: string;
      outputSlice: string;
      found: boolean;
      reason?: string;
    }>('tool.result', { toolCallId: props.tool.id, offset: 2000, limit: 8000 });
    if (!r.found) {
      // N5：尾部可无（released/running）→ 显示“已随压缩/清理释放”，不抛错
      const reason = (r as unknown as { reason?: string }).reason;
      if (reason === 'released') {
        messages.toolTail.set(props.tool.id, { loading: false, error: 'released' });
      } else {
        messages.toolTail.set(props.tool.id, { loading: false });
      }
      return;
    }
    messages.toolTail.set(props.tool.id, {
      loading: false,
      argsRest: (r.argsSlice ?? '').slice(0, 8000),
      outputRest: (r.outputSlice ?? '').slice(0, 8000),
      error: null,
    });
  } catch {
    // N4：拉失败回落头部
    messages.toolTail.set(props.tool.id, { loading: false, error: 'fetch-failed' });
  }
}

// 展开才请求（ToolCard 展开时挂载本组件，故 mount 即展开态）
watch(
  () => [props.tool.status, props.tool.truncated],
  () => {
    void fetchTail();
  },
  { immediate: true }
);
</script>

<template>
  <div class="tool-body">
    <section class="body-section">
      <div class="section-title">参数<span v-if="tool.argsTotal">（共 {{ tool.argsTotal }} 字符）</span></div>
      <pre class="args-pre">{{ shownArgs }}</pre>
      <button
        v-if="argsTruncated && !argsExpanded"
        type="button"
        class="args-more"
        @click="argsExpanded = true; fetchTail()"
      >
        展开全部参数（共 {{ prettyArgs.length }} 字符）
      </button>
      <span v-if="loadingTail" class="tail-loading">尾部加载中…</span>
      <span v-if="tailError === 'released'" class="tail-released">已随压缩/清理释放</span>
      <button v-if="tailError === 'fetch-failed'" type="button" class="args-more" @click="fetchTail()">
        尾部拉取失败，重试
      </button>
    </section>

    <section v-if="liveOutput" class="body-section">
      <div class="section-title">输出</div>
      <ToolOutput :output="liveOutput" />
    </section>
    <section v-else-if="tool.status === 'running'" class="body-section pending-hint">
      正在执行，等待输出…
    </section>

    <template v-if="result">
      <ToolDiff v-if="diffFirst" :output="result" />
      <section class="body-section">
        <div class="section-title">{{ isError ? '错误' : '结果' }}<span v-if="tool.outputTotal">（共 {{ tool.outputTotal }} 字符）</span></div>
        <ToolOutput :output="result" />
        <span v-if="loadingTail" class="tail-loading">尾部加载中…</span>
        <span v-if="tailError === 'released'" class="tail-released">已随压缩/清理释放</span>
      </section>
      <ToolDiff v-if="!diffFirst" :output="result" />
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
.tail-loading,
.tail-released {
  font-size: var(--text-xs);
  color: hsl(var(--fg-muted));
  margin-left: var(--space-2);
}
</style>

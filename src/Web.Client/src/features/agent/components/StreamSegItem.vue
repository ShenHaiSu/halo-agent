<template>
  <div
    v-if="seg.kind === 'reasoning' && seg.text"
    class="whitespace-pre-wrap break-words app-text-small text-[var(--reasoning-text)] max-h-[40vh] overflow-y-auto reasoning-seg"
    :data-seg="seg.key"
  >
    {{ seg.text }}
  </div>
  <div v-else-if="seg.kind === 'tool' && seg.card" data-testid="active-turn-tools" :data-seg="seg.key">
    <ToolCallCard v-if="hasToolContent" :card="seg.card" :default-collapsed="true" />
    <!-- P1：参数/结果尚未到达时卡片正文是空的，不再渲染可展开的空折叠，先占位明示接收中 -->
    <div
      v-else
      class="rounded-md border border-subtle bg-surface px-2.5 py-1.5 text-[11px] text-secondary flex items-center gap-2 min-w-0"
      data-testid="tool-card-pending"
    >
      <span class="tool-pending-dot" aria-hidden="true" />
      <span class="font-mono truncate">{{ seg.card.functionName || 'tool' }} · 参数接收中…</span>
    </div>
  </div>
  <div
    v-else-if="seg.kind === 'text' && seg.text"
    class="app-text-body leading-relaxed text-primary break-words select-text min-w-0"
    :data-seg="seg.key"
    @click="emit('link-click', $event)"
  >
    <MarkdownStreamBlocks v-if="!streamMdKillSwitch" :text="seg.text" :cache-key="seg.key" />
    <div v-else style="white-space: pre-wrap" v-html="renderStream(seg.text)" />
    <!-- M2-5/M3-5：流式光标贴在块容器之后（不插进 v-html 字符串，避免破坏块缓存） -->
    <span v-if="isTail && isGenerating" class="smooth-cursor" aria-hidden="true" />
  </div>
</template>
<script setup lang="ts">
/**
 * M3-2：活动容器的单个流式段（纯搬迁自 TimelineView.vue，为 TransitionGroup 抽出单根子组件）。
 *
 * 为什么必须抽：`v-for` 与 `v-if` 嵌在 `<template>` 上时无法直接套 `TransitionGroup`
 * （后者要求每个子节点是单一组件），所以把「三种 seg 的分支渲染」收进这里。
 */
import { computed } from 'vue';
import type { DisplaySeg } from '../composables/useTimeline';
import ToolCallCard from './ToolCallCard.vue';
import MarkdownStreamBlocks from './MarkdownStreamBlocks.vue';
import '@features/agent/composables/smooth-cursor.css';

const props = defineProps<{
  seg: DisplaySeg;
  /** need23-R5：流式 Markdown 逃生开关（true=回落纯文本）。 */
  streamMdKillSwitch: boolean;
  renderStream: (text: string) => string;
  /** 是否为 segs 中最后一个（光标只在尾段显示）。 */
  isTail: boolean;
  isGenerating: boolean;
}>();
const emit = defineEmits<{ 'link-click': [e: MouseEvent] }>();
/** P1：空卡判定——running 且参数/结果都还没到时不渲染空折叠。终态（done/error）恒展示卡片（状态本身即信息）。 */
const hasToolContent = computed(() => {
  const c = props.seg.card as unknown as { argsJson?: unknown; resultText?: unknown; status?: unknown } | null;
  if (!c) return false;
  if (String(c.status || '') !== 'running') return true;
  return String(c.argsJson || '').trim().length > 0 || String(c.resultText || '').trim().length > 0;
});
</script>
<style scoped>
.reasoning-seg {
  /* 与 TimelineView 中的同类声明一致：滚动条出现时宽度不变 */
  scrollbar-gutter: stable;
  overflow-anchor: auto;
}
/* P1：空工具卡占位圆点（参数接收中呼吸态；终态卡片不受影响） */
.tool-pending-dot {
  width: 7px;
  height: 7px;
  border-radius: 9999px;
  background: #3b82f6;
  flex-shrink: 0;
  animation: tool-pending-pulse 1.2s ease-in-out infinite;
}
@keyframes tool-pending-pulse {
  0%,
  100% {
    opacity: 1;
    transform: scale(1);
  }
  50% {
    opacity: 0.45;
    transform: scale(0.8);
  }
}
@media (prefers-reduced-motion: reduce) {
  .tool-pending-dot {
    animation: none;
  }
}
</style>

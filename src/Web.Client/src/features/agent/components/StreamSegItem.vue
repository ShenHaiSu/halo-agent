<template>
  <div
    v-if="seg.kind === 'reasoning' && seg.text"
    class="whitespace-pre-wrap break-words app-text-small text-[var(--reasoning-text)] max-h-[40vh] overflow-y-auto reasoning-seg"
    :data-seg="seg.key"
  >
    {{ seg.text }}
  </div>
  <div v-else-if="seg.kind === 'tool' && seg.card" data-testid="active-turn-tools" :data-seg="seg.key">
    <ToolCallCard :card="seg.card" :default-collapsed="true" />
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
import type { DisplaySeg } from '../composables/useTimeline';
import ToolCallCard from './ToolCallCard.vue';
import MarkdownStreamBlocks from './MarkdownStreamBlocks.vue';
import '@features/agent/composables/smooth-cursor.css';

defineProps<{
  seg: DisplaySeg;
  /** need23-R5：流式 Markdown 逃生开关（true=回落纯文本）。 */
  streamMdKillSwitch: boolean;
  renderStream: (text: string) => string;
  /** 是否为 segs 中最后一个（光标只在尾段显示）。 */
  isTail: boolean;
  isGenerating: boolean;
}>();
const emit = defineEmits<{ 'link-click': [e: MouseEvent] }>();
</script>
<style scoped>
.reasoning-seg {
  /* 与 TimelineView 中的同类声明一致：滚动条出现时宽度不变 */
  scrollbar-gutter: stable;
  overflow-anchor: auto;
}
</style>

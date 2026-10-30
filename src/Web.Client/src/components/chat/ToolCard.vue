<!-- src/Web.Client/src/components/chat/ToolCard.vue -->
<!-- P5 D5-4 工具卡片：6 状态（pending/running/awaiting-user/done/error/cancelled），
     状态从 ToolCallInfo 派生（优先级 cancelled > error > awaiting-user > running >
     pending > done）；默认展开规则 + 用户折叠记忆（本模块 collapsedToolIds，
     语义同 ui.collapsedTools）；silentAlert 黄/红预警条。 -->
<script setup lang="ts">
import { computed } from 'vue';
import type { ToolCallInfo } from '@/stores/messages';
import { usePermissionsStore } from '@/stores/permissions';
import { collapsedToolIds, deriveState, toolMeta } from './toolMeta';
import { summarizeArgs } from './toolArgs';
import HaloIcon from '@/components/icons/HaloIcon.vue';
import HBadge from '@/components/base/HBadge.vue';
import HSpinner from '@/components/base/HSpinner.vue';
import ToolCardBody from './ToolCardBody.vue';
import UnknownToolCard from './UnknownToolCard.vue';

const props = defineProps<{ tool: ToolCallInfo; isLive: boolean }>();

const perms = usePermissionsStore();

const kind = computed(() => (props.tool.kind === 'unknown' ? 'unknown' : 'function'));
const meta = computed(() => toolMeta(props.tool.name, kind.value));
const displayName = computed(() =>
  kind.value === 'unknown' ? (props.tool.rawType ?? props.tool.name) : meta.value.label
);
const summary = computed(() => summarizeArgs(props.tool.name, props.tool.arguments, kind.value));

// awaiting-user 判定：P2 单槽串行，最新 running 的卡片 + 有非 rounds 待答询问即视为等待授权
const isAwaiting = computed(
  () =>
    props.tool.status === 'running' &&
    perms.pending !== null &&
    perms.pending.kind !== 'agent.rounds'
);

const state = computed(() => deriveState(props.tool, isAwaiting.value));

// 默认展开规则：
//   error / awaiting-user → 展开（用户必须看到）
//   running（有输出或流式中）→ 展开（实时看进度）
//   done → 折叠（结果太多，默认不看）
// 用户手动折叠优先于默认。
const userCollapsed = computed(() => collapsedToolIds.has(props.tool.id));
const expanded = computed(() => {
  if (state.value === 'awaiting-user') return true;
  if (userCollapsed.value) return false;
  if (state.value === 'error') return true;
  if (state.value === 'running' && (props.tool.output || props.isLive)) return true;
  return false;
});

function toggle(): void {
  if (state.value === 'awaiting-user') return;
  if (userCollapsed.value) collapsedToolIds.delete(props.tool.id);
  else collapsedToolIds.add(props.tool.id);
}

function onHeadKeydown(e: KeyboardEvent): void {
  if (state.value === 'awaiting-user') return;
  const btn = e.currentTarget as HTMLElement | null;
  if (e.key === 'ArrowRight' && !expanded.value) {
    e.preventDefault();
    collapsedToolIds.delete(props.tool.id);
    btn?.focus();
  } else if (e.key === 'ArrowLeft' && expanded.value) {
    e.preventDefault();
    collapsedToolIds.add(props.tool.id);
    btn?.focus();
  }
}

const statusText = computed(() => {
  switch (state.value) {
    case 'pending':
      return '排队中';
    case 'running':
      return '执行中';
    case 'awaiting-user':
      return '等待授权';
    case 'done':
      return '成功';
    case 'error':
      return '失败';
    case 'cancelled':
      return '已中断';
    default:
      return '未知';
  }
});

const badgeVariant = computed(() => {
  switch (state.value) {
    case 'pending':
      return 'default' as const;
    case 'running':
      return 'accent' as const;
    case 'awaiting-user':
      return 'warning' as const;
    case 'done':
      return 'success' as const;
    case 'error':
      return 'danger' as const;
    case 'cancelled':
      return 'default' as const;
    default:
      return 'default' as const;
  }
});
</script>

<template>
  <div class="tool-card" :class="`st-${state}`">
    <button
      class="tool-head"
      type="button"
      :aria-expanded="expanded"
      :aria-label="`${displayName}（${statusText}）：${summary}`"
      @click="toggle"
      @keydown="onHeadKeydown"
    >
      <HaloIcon :name="meta.icon" :size="15" class="tool-icon" />
      <span class="tool-name">{{ displayName }}</span>
      <span class="tool-summary" :title="summary">{{ summary }}</span>
      <span class="tool-spacer" />
      <HSpinner v-if="state === 'running'" size="xs" />
      <HBadge :variant="badgeVariant" :content="statusText" />
      <HaloIcon :name="expanded ? 'chevron-down' : 'chevron-right'" :size="14" />
    </button>

    <!-- silentAlert 预警条 -->
    <div v-if="tool.alertLevel === 2" class="tool-alert danger" role="alert">
      <HaloIcon name="alert" :size="14" />
      <span>该命令已运行较久，可能卡住。可按 Esc 中断。</span>
    </div>
    <div v-else-if="tool.alertLevel === 1" class="tool-alert warn" role="status">
      <HaloIcon name="clock" :size="14" />
      <span>该命令已运行较久，请耐心等待。</span>
    </div>

    <ToolCardBody v-if="expanded && kind === 'function'" :tool="tool" />
    <UnknownToolCard v-if="expanded && kind === 'unknown'" :tool="tool" />
  </div>
</template>

<style scoped>
.tool-card {
  border: 1px solid hsl(var(--border));
  border-radius: var(--radius-md);
  overflow: hidden;
  background: hsl(var(--bg-elevated));
}
.st-error {
  border-color: hsl(var(--danger) / 0.5);
}
.st-awaiting-user {
  border-color: hsl(var(--warning) / 0.6);
}
.tool-head {
  display: flex;
  align-items: center;
  gap: var(--space-2);
  width: 100%;
  padding: var(--space-2) var(--space-3);
  background: transparent;
  border: 0;
  cursor: pointer;
  text-align: left;
  color: hsl(var(--fg));
  font-size: var(--text-sm);
  min-width: 0;
}
.tool-head:hover {
  background: hsl(var(--bg-subtle));
}
.tool-icon {
  color: hsl(var(--fg-muted));
  flex: none;
}
.tool-name {
  font-weight: 500;
  flex: none;
}
.tool-summary {
  color: hsl(var(--fg-muted));
  font-family: var(--font-mono);
  font-size: var(--text-xs);
  min-width: 0;
  overflow: hidden;
  text-overflow: ellipsis;
  white-space: nowrap;
}
.tool-spacer {
  flex: 1;
  min-width: 0;
}
.tool-alert {
  display: flex;
  align-items: center;
  gap: var(--space-2);
  padding: var(--space-1) var(--space-3);
  font-size: var(--text-xs);
}
.tool-alert.warn {
  background: hsl(var(--warning-subtle));
  color: hsl(var(--warning));
}
.tool-alert.danger {
  background: hsl(var(--danger-subtle));
  color: hsl(var(--danger));
}
</style>

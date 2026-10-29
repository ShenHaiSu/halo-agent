<!-- src/Web.Client/src/components/chat/MessageItem.vue -->
<!-- P5 D5-3 单条消息：只有 assistant + 流式中才走增量解析，其余一次性解析。
     流结束时 finalize 做一次完整解析并替换；roleLabel 用 computed（无 options API）。 -->
<script setup lang="ts">
import { computed, watch } from 'vue';
import type { MessageItem as Msg } from '@/stores/messages';
import { useMessagesStore } from '@/stores/messages';
import { transport } from '@/transport';
import { formatRelativeTime } from '@/utils/format';
import { parseMarkdown } from './MarkdownRenderer';
import { useStreamingMarkdown } from '@/components/chat/useStreamingMarkdown';
import MessageAvatar from './MessageAvatar.vue';
import MessageActions from './MessageActions.vue';
import MessageBody from './MessageBody.vue';
import ReasoningBlock from './ReasoningBlock.vue';
import StreamingCursor from './StreamingCursor.vue';
import ToolCard from './ToolCard.vue';

const props = defineProps<{
  message: Msg;
  isStreaming: boolean;
}>();

const messages = useMessagesStore();

// 只有 assistant + 流式中才走增量解析；用户消息/历史消息一次性解析
const isLive = computed(() => props.isStreaming && props.message.role === 'assistant');

const md = useStreamingMarkdown(() => props.message.content);

// 非流式：直接完整解析（内容已完整，用 lenient=false）
const finalNodes = computed(() => parseMarkdown(props.message.content));

const nodes = computed(() => (isLive.value ? md.committed.value : finalNodes.value));

// 流结束时做一次完整解析并替换
watch(
  () => props.isStreaming,
  (now, before) => {
    if (before && !now) md.finalize();
}
);

const roleLabel = computed(() => {
  switch (props.message.role) {
    case 'user':
      return '你';
    case 'assistant':
      return 'HaloAgent';
    case 'tool':
      return '工具';
    default:
      return '系统';
}
});

const injected = computed(
  () => (props.message as unknown as { injected?: boolean }).injected === true
);

const truncated = computed(() => props.message.truncatedByLength === true);
const userAborted = computed(() => props.message.truncatedByUser === true);

async function retry(): Promise<void> {
  // 找到该 assistant 消息前最近的一条 user 消息，重发
  const idx = messages.items.findIndex((m) => m.id === props.message.id);
  const user = [...messages.items.slice(0, idx)].reverse().find((m) => m.role === 'user');
  if (!user) return;
  await transport.request('agent.prompt', { text: user.content });
}
</script>

<template>
<article
    class="msg-item"
:class="[`role-${message.role}`, { streaming: isStreaming }]"
:data-message-id="message.id"
  >
<MessageAvatar :role="message.role" />

<div class="msg-main">
<header class="msg-head">
<span class="msg-role">{{ roleLabel }}</span>
<span class="msg-time">{{ formatRelativeTime(message.ts) }}</span>
<span v-if="injected" class="msg-badge">已注入</span>
<span class="msg-head-spacer" />
<MessageActions v-if="!isStreaming" :message="message" @retry="retry" />
</header>

<!-- 思考内容 -->
<ReasoningBlock v-if="message.reasoning" :text="message.reasoning" :live="isStreaming" />

<!-- 正文 -->
<MessageBody
        v-if="message.content"
:nodes="nodes"
:pending="isLive ? md.pendingText.value : ''"
      />

<!-- 流式光标 -->
<StreamingCursor v-if="isStreaming && !message.content" />

<!-- 工具卡片 -->
<div v-if="message.toolCalls?.length" class="msg-tools">
<ToolCard v-for="tc in message.toolCalls" :key="tc.id" :tool="tc" :is-live="isStreaming" />
</div>

<!-- 截断提示 -->
<div v-if="truncated" class="msg-warn">回答因达到最大长度而被截断。</div>
<div v-else-if="userAborted" class="msg-warn">已由你中断。</div>
</div>
</article>
</template>
 
<style scoped>
.msg-item {
display: flex;
gap: var(--space-3);
min-width: 0;
width: 100%;
}
.role-user {
flex-direction: row-reverse;
}
.msg-main {
flex: 1;
min-width: 0; /* * 防止长内容撑破容器 */
display: flex;
flex-direction: column;
gap: var(--space-2);
}
.role-user .msg-main {
align-items: flex-end;
}
.msg-head {
display: flex;
align-items: center;
gap: var(--space-2);
font-size: var(--text-xs);
color: hsl(var(--fg-subtle));
min-width: 0;
}
.role-user .msg-head {
flex-direction: row-reverse;
}
.msg-role {
font-weight: 500;
}
.msg-time {
font-size: var(--text-2xs);
}
.msg-badge {
font-size: var(--text-2xs);
padding: 0 var(--space-1);
border-radius: var(--radius-xs);
background: hsl(var(--bg-muted));
color: hsl(var(--fg-muted));
}
.msg-head-spacer {
flex: 1;
min-width: 0;
}
.msg-tools {
display: flex;
flex-direction: column;
gap: var(--space-2);
}
.msg-warn {
font-size: var(--text-xs);
color: hsl(var(--warning));
padding: var(--space-1) var(--space-2);
background: hsl(var(--warning-subtle));
border-radius: var(--radius-sm);
}
/* 用户消息气泡：限宽 560px（与 chat.css 全局一致），行占满但气泡右对齐收敛 */
.role-user :deep(.md-body) {
background: hsl(var(--accent-subtle));
border: 1px solid hsl(var(--border-subtle));
border-radius: var(--radius-md);
padding: var(--space-2) var(--space-3);
max-width: min(100%, 560px);
}
</style>

<!-- src/Web.Client/src/components/chat/TurnCard.vue -->
<!-- P15 TurnCard 三段式：用户消息（+排队 injected） / 过程 details（思考+中途正文+工具卡） / 末轮正文 -->
<script setup lang="ts">
import { computed, nextTick, ref, watch } from 'vue';
import type { TurnView } from '@/stores/messages';
import { useMessagesStore } from '@/stores/messages';
import { useStreamStore } from '@/stores/stream';
import { usePermissionsStore } from '@/stores/permissions';
import { parseMarkdown } from './MarkdownRenderer';
import { useStreamingMarkdown } from '@/components/chat/useStreamingMarkdown';
import MessageBody from './MessageBody.vue';
import ReasoningBlock from './ReasoningBlock.vue';
import StreamingCursor from './StreamingCursor.vue';
import ToolCard from './ToolCard.vue';
import MessageActions from './MessageActions.vue';
import { formatRelativeTime } from '@/utils/format';
import { transport } from '@/transport';

const props = defineProps<{ turn: TurnView }>();
const messages = useMessagesStore();
const stream = useStreamStore();
const perms = usePermissionsStore();

const isActive = computed(() => stream.currentTurn?.turnId === props.turn.turnId);

// 草稿：当前 turn 所有 used 的 text/reasoning（流式可见）
const draftsForTurn = computed(() => {
  const out: Array<{ used: number; text: string; reasoning: string }> = [];
  for (const [k, v] of Object.entries(messages.drafts)) {
    const [tid, usedStr] = k.split(':');
    if (tid !== props.turn.turnId) continue;
    out.push({ used: Number(usedStr ?? '0'), text: v.text, reasoning: v.reasoning });
  }
  out.sort((a, b) => a.used - b.used);
  return out;
});
const liveText = computed(() => draftsForTurn.value.map((d) => d.text).join(''));
const liveReasoning = computed(() => draftsForTurn.value.map((d) => d.reasoning).join(''));

// 统一稳态渲染：末轮全文 parse；流式节流渐进
const liveMd = useStreamingMarkdown(() => liveText.value);
const finalNodes = computed(() => (props.turn.final.content ? parseMarkdown(props.turn.final.content) : []));
const liveNodes = computed(() => liveMd.nodes.value);

// 过程展开三态
const mode = computed(() => messages.processOpen[props.turn.turnId] ?? 'auto');
const processOpen = computed(() =>
  mode.value === 'userClosed' ? false : mode.value === 'userOpen' ? true : isActive.value
);
const userToggling = ref(false);
function onToggle(e: Event): void {
  // Chrome 程序化 open 也触发 toggle：防抖，只记用户手势
  if (userToggling.value) return;
  userToggling.value = true;
  const el = e.target as HTMLDetailsElement | null;
  const open = el?.open ?? !processOpen.value;
  messages.processOpen[props.turn.turnId] = open ? 'userOpen' : 'userClosed';
  setTimeout(() => { userToggling.value = false; }, 0);
}

// N2：ask.permission（非 rounds）强制弹开并定位
watch(
  () => perms.pending,
  (p) => {
    if (p && p.kind !== 'agent.rounds') {
      messages.processOpen[props.turn.turnId] = 'userOpen';
      void nextTick(() => {
        document.querySelector(`[data-turn="${props.turn.turnId}"] .turn-process`)?.scrollIntoView({ block: 'nearest' });
      });
    }
  }
);

// N3：搬运/折叠合并同一 nextTick（此处 finalize 已在 store 同帧完成，仅保证一次布局）
watch(isActive, () => {
  void nextTick(() => {});
});

const processCount = computed(() => {
  let n = props.turn.process.length;
  if (liveText.value || liveReasoning.value) n += draftsForTurn.value.length;
  const tools = (props.turn.final.toolCalls ?? []).length;
  return n + tools;
});
const truncated = computed(() => props.turn.final.truncatedByLength === true);

async function retry(): Promise<void> {
  await transport.request('agent.prompt', { text: props.turn.user.content });
}
</script>

<template>
  <article class="turn-card" :data-turn="turn.turnId">
    <!-- 用户消息 + 时间/复制（绑定 turn.user，与 final 独立） -->
    <div v-if="turn.user.content" class="turn-user-block">
      <div class="turn-user user-plain">{{ turn.user.content }}</div>
      <div class="turn-user-meta">
        <span class="msg-time">{{ formatRelativeTime(turn.user.ts) }}</span>
        <MessageActions :message="turn.user" />
      </div>
    </div>
    <div v-for="u in turn.injected" :key="u.id" class="turn-user user-plain injected">
      {{ u.content }}<span class="msg-badge">已注入</span>
    </div>

    <!-- 过程（思考 + 中途正文 + 工具卡）：流式默认展开，结束后自动折叠 -->
    <details v-if="processCount > 0 || isActive" class="turn-process" :open="processOpen" @toggle="onToggle">
      <summary>过程 · {{ processCount }} 段</summary>
      <div class="process-body">
        <div v-for="m in turn.process" :key="m.id" class="process-item">
          <ReasoningBlock v-if="m.reasoning" :text="m.reasoning" :live="false" :kind="m.reasoningKind ?? 'full'" />
          <MessageBody v-if="m.content" :nodes="parseMarkdown(m.content)" :pending="''" />
          <div v-if="m.toolCalls?.length" class="msg-tools">
            <ToolCard v-for="tc in m.toolCalls" :key="tc.id" :tool="tc" :is-live="false" />
          </div>
        </div>
        <!-- 流式草稿（未搬运前可见） -->
        <!-- 流式草稿（未搬运前可见）：正文合并渲染一次，多 used 不重复 -->
        <template v-for="d in draftsForTurn" :key="`draft-${d.used}`">
          <ReasoningBlock v-if="d.reasoning" :text="d.reasoning" :live="true" kind="full" />
        </template>
        <MessageBody v-if="liveText" :nodes="liveNodes" :pending="liveMd.pendingText.value" />
        <!-- 当前 final 上的 running 工具卡（未搬运） -->
        <div v-if="turn.final.toolCalls?.length" class="msg-tools">
          <ToolCard v-for="tc in turn.final.toolCalls" :key="tc.id" :tool="tc" :is-live="isActive" />
        </div>
      </div>
    </details>

    <!-- 末轮正文：Markdown 全量解析，默认唯一展开 -->
    <div class="turn-final">
      <MessageBody v-if="turn.final.content" :nodes="finalNodes" :pending="isActive ? liveMd.pendingText.value : ''" />
      <StreamingCursor v-if="isActive && !turn.final.content && !liveText" />
      <div v-if="truncated" class="msg-warn">回答因达到最大长度而被截断。</div>
      <div class="turn-meta">
        <span class="msg-time">{{ formatRelativeTime(turn.final.ts) }}</span>
        <MessageActions v-if="!isActive" :message="turn.final" @retry="retry" />
      </div>
    </div>
  </article>
</template>

<style scoped>
.turn-card {
  display: flex;
  flex-direction: column;
  gap: var(--space-2);
  /* 长会话性能：视口外的卡片跳过渲染，滚动时按需绘制 */
  content-visibility: auto;
  contain-intrinsic-size: auto 240px;
}
.turn-user.user-plain {
  background: hsl(var(--accent-subtle));
  border: 1px solid hsl(var(--border-subtle));
  border-radius: var(--radius-md);
  padding: var(--space-2) var(--space-3);
  max-width: min(100%, 560px);
  align-self: flex-end;
  white-space: pre-wrap;
  overflow-wrap: anywhere;
}
.turn-user.injected {
  opacity: 0.85;
}
.turn-user-block {
  display: flex;
  flex-direction: column;
  align-items: flex-end;
  gap: var(--space-1);
  max-width: min(100%, 560px);
  align-self: flex-end;
  min-width: 0;
}
.turn-user-block .turn-user.user-plain {
  max-width: 100%;
}
.turn-user-meta {
  display: flex;
  align-items: center;
  gap: var(--space-2);
  font-size: var(--text-xs);
  color: hsl(var(--fg-subtle));
}
.turn-user-meta .msg-time {
  font-size: var(--text-2xs);
}
.msg-badge {
  font-size: var(--text-2xs);
  padding: 0 var(--space-1);
  border-radius: var(--radius-xs);
  background: hsl(var(--bg-muted));
  color: hsl(var(--fg-muted));
  margin-left: var(--space-2);
}
.turn-process {
  border: 1px solid hsl(var(--border-subtle));
  border-radius: var(--radius-md);
  background: hsl(var(--bg-muted));
  padding: var(--space-1) var(--space-2);
}
.turn-process summary {
  cursor: pointer;
  font-size: var(--text-xs);
  color: hsl(var(--fg-muted));
}
.process-body {
  display: flex;
  flex-direction: column;
  gap: var(--space-2);
  margin-top: var(--space-2);
}
.turn-process:not([open]) .process-body {
  display: none;
}
.msg-tools {
  display: flex;
  flex-direction: column;
  gap: var(--space-2);
}
.msg-warn {
  font-size: var(--text-xs);
  color: hsl(var(--warning));
}
.turn-meta {
  display: flex;
  align-items: center;
  gap: var(--space-2);
  font-size: var(--text-xs);
  color: hsl(var(--fg-subtle));
}
</style>

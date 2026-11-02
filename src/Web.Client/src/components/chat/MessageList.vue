<!-- src/Web.Client/src/components/chat/MessageList.vue -->
<!-- P15：渲染 TurnCard 列表，窗口化以 currentTurn.turnId 为中心 -->
<script setup lang="ts">
import { computed, nextTick, ref, watch } from 'vue';
import { throttle } from '@/utils/throttle';
import { useMessagesStore } from '@/stores/messages';
import { useStreamStore } from '@/stores/stream';
import { useSessionsStore } from '@/stores/sessions';
import { useMessageWindow } from '@/composables/useMessageWindow';
import HScroll from '@/components/base/HScroll.vue';
import HEmpty from '@/components/base/HEmpty.vue';
import HButton from '@/components/base/HButton.vue';
import HSkeleton from '@/components/base/HSkeleton.vue';
import TurnCard from './TurnCard.vue';
import CompactDivider from './CompactDivider.vue';

const emit = defineEmits<{ (e: 'use-sample', text: string): void }>();

const messages = useMessagesStore();
const stream = useStreamStore();
const sessions = useSessionsStore();

const currentTurnId = computed(() => stream.currentTurn?.turnId ?? null);
const switching = computed(() => sessions.switching);

const scroller = ref<InstanceType<typeof HScroll> | null>(null);

const win = useMessageWindow(
  computed(() => messages.turns),
  currentTurnId
);

/** 展开全部（用户点“显示前 N 条”时） */
const showAll = ref(false);
const visible = computed(() =>
  showAll.value ? { items: messages.turns, hiddenTop: 0, hiddenBottom: 0 } : win.value
);

/** 自动滚动的唯一判据：只在本来就在底部时跟随 */
function followIfNeeded(): void {
  void nextTick(() => {
    const s = scroller.value as unknown as {
      isAtBottom: () => boolean;
      scrollToBottom: (smooth?: boolean) => void;
    } | null;
    if (!s) return;
    if (typeof s.isAtBottom === 'function') {
      if (!s.isAtBottom()) return;
    }
    s.scrollToBottom(false);
  });
}

const throttledFollow = throttle(() => followIfNeeded(), 150);

watch(() => messages.turns.length, followIfNeeded);
watch(() => messages.turns[messages.turns.length - 1]?.final.content.length ?? 0, followIfNeeded);
const lastToolOutputLen = computed(() =>
  (messages.turns[messages.turns.length - 1]?.final.toolCalls ?? []).reduce(
    (n, t) => n + (t.output?.length ?? 0) + (t.result?.length ?? 0),
    0
  )
);
watch(lastToolOutputLen, throttledFollow);

// 切会话即重置展开态并回顶（instant，避免 smooth 强制布局掉帧）
watch(
  () => sessions.currentId,
  () => {
    showAll.value = false;
    const s = scroller.value as unknown as { scrollToTop?: (smooth?: boolean) => void } | null;
    s?.scrollToTop?.(false);
  },
);

const samples = [
  '帮我看看这个项目用了哪些依赖',
  '找出所有 TODO 并整理成清单',
  '这个文件里的报错是什么原因',
];
</script>

<template>
  <div class="msg-wrap" :class="{ 'is-switching': switching }">
    <div v-if="switching" class="msg-switch-bar" aria-hidden="true">
      <div class="msg-switch-bar-inner" />
    </div>
    <HScroll ref="scroller" data-scroll="messages" class="msg-scroll h-full min-h-0 flex-1">
      <div class="msg-inner density-compact">
        <div v-if="visible.hiddenTop > 0 && !showAll" class="msg-fold">
          <HButton variant="ghost" size="sm" @click="showAll = true">
            显示更早的 {{ visible.hiddenTop }} 条
          </HButton>
        </div>

        <template v-for="t in visible.items" :key="t.turnId">
          <CompactDivider v-if="t.dividerBefore" :divider="t.dividerBefore" />
          <TurnCard :turn="t" />
        </template>

        <div v-if="visible.hiddenBottom > 0 && !showAll" class="msg-fold">
          <HButton variant="ghost" size="sm" @click="showAll = true">
            显示更新的 {{ visible.hiddenBottom }} 条
          </HButton>
        </div>

        <!-- 有旧内容驻留时不挂骨架（省 DOM、省抖动），只在真正的空页加载时显示 -->
        <div v-if="messages.loading && messages.isEmpty" class="msg-loading-list">
          <HSkeleton shape="block" height="72px" />
          <HSkeleton shape="block" height="120px" />
          <HSkeleton shape="block" height="56px" />
        </div>

        <div v-if="messages.isEmpty && !messages.loading" class="msg-empty">
          <HEmpty
            icon="message"
            title="开始新的对话"
            description="描述你想让 HaloAgent 做的事，它会在需要时请求你的许可。"
          >
            <template #action>
              <div class="empty-samples">
                <HButton
                  v-for="(s, i) in samples"
                  :key="i"
                  variant="ghost"
                  size="sm"
                  @click="emit('use-sample', s)"
                >
                  {{ s }}
                </HButton>
              </div>
            </template>
          </HEmpty>
        </div>

        <div data-scroll-anchor class="msg-anchor" />
      </div>
    </HScroll>
  </div>
</template>

<style scoped>
.msg-wrap {
  position: relative;
  display: flex;
  flex: 1;
  min-height: 0;
  flex-direction: column;
}
.msg-scroll {
  flex: 1;
  min-height: 0;
  height: 100%;
}
.msg-inner {
  width: 100%;
  max-width: 100%;
  margin: 0;
  padding: var(--space-4) var(--space-6) var(--space-6);
  display: flex;
  flex-direction: column;
  gap: var(--space-4);
  min-height: 100%;
  min-width: 0;
  /* 250ms 柔和过渡：只动 opacity（合成器层，不触发重排） */
  opacity: 1;
  transition: opacity 250ms ease;
}
.msg-wrap.is-switching .msg-inner {
  opacity: 0.35;
  pointer-events: none;
}
.msg-fold {
  display: flex;
  justify-content: center;
}
.msg-empty {
  flex: 1;
  display: flex;
  align-items: center;
  justify-content: center;
}
.empty-samples {
  display: flex;
  flex-direction: column;
  gap: var(--space-2);
  margin-top: var(--space-3);
}
.msg-loading-list {
  display: flex;
  flex-direction: column;
  gap: var(--space-3);
}
.msg-anchor {
  height: 1px;
}
/* 顶部进度条：只用 transform 位移动画，走 GPU，长列表切换也不掉帧 */
.msg-switch-bar {
  position: absolute;
  top: 0;
  left: 0;
  right: 0;
  height: 2px;
  overflow: hidden;
  z-index: 5;
  background: transparent;
}
.msg-switch-bar-inner {
  height: 100%;
  width: 40%;
  border-radius: 999px;
  background: hsl(var(--accent));
  animation: msg-switch-slide 1s ease-in-out infinite;
  will-change: transform;
}
@keyframes msg-switch-slide {
  0% { transform: translateX(-100%); }
  100% { transform: translateX(250%); }
}
@media (prefers-reduced-motion: reduce) {
  .msg-inner { transition: none; }
  .msg-switch-bar-inner { animation: none; }
  .msg-wrap.is-switching .msg-inner { opacity: 1; }
}
</style>

<!-- src/Web.Client/src/components/chat/MessageList.vue -->
<!-- P5 D5-3 消息列表（见 03 §5.2 最终版）：窗口化渲染 + 顶部/底部折叠提示 +
     压缩摘要卡 + 空态示例 + 加载骨架 + 自动跟随到底。 -->
<script setup lang="ts">
import { computed, nextTick, ref, watch } from 'vue';
import { useMessagesStore } from '@/stores/messages';
import { useStreamStore } from '@/stores/stream';
import { useSessionStore } from '@/stores/session';
import { useMessageWindow } from '@/composables/useMessageWindow';
import HScroll from '@/components/base/HScroll.vue';
import HEmpty from '@/components/base/HEmpty.vue';
import HButton from '@/components/base/HButton.vue';
import HSkeleton from '@/components/base/HSkeleton.vue';
import MessageItem from './MessageItem.vue';
import CompactionCard from './CompactionCard.vue';

const emit = defineEmits<{ (e: 'use-sample', text: string): void }>();

const messages = useMessagesStore();
const stream = useStreamStore();
const session = useSessionStore();

// messages store 暂无 streamingId 字段（不改 stores）：用 stream.streamingMessageId 判定
const streamingId = computed(() => stream.streamingMessageId);

const scroller = ref<InstanceType<typeof HScroll> | null>(null);

const win = useMessageWindow(
  computed(() => messages.items),
  streamingId
);

/** 展开全部（用户点"显示前 N 条"时） */
const showAll = ref(false);
const visible = computed(() =>
  showAll.value ? { items: messages.items, hiddenTop: 0, hiddenBottom: 0 } : win.value
);

/** 自动滚动的唯一判据：只在本来就在底部时跟随（调 HScroll.isAtBottom 方法，避免直接读 Ref 误判） */
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

watch(() => messages.items.length, followIfNeeded);
watch(() => messages.items[messages.items.length - 1]?.content.length ?? 0, followIfNeeded);

const samples = [
  '帮我看看这个项目用了哪些依赖',
  '找出所有 TODO 并整理成清单',
  '这个文件里的报错是什么原因',
];
</script>

<template>
  <!-- 滚动只发生在消息区：HScroll 内层容器是唯一 overflow-y:auto（auto-follow 由 followIfNeeded 显式触发） -->
  <HScroll ref="scroller" data-scroll="messages" class="msg-scroll h-full min-h-0 flex-1">
    <div class="msg-inner">
      <!-- 压缩摘要卡（在消息最上方） -->
      <CompactionCard v-if="session.compactionSummary" :summary="session.compactionSummary" />

      <!-- 顶部折叠提示 -->
      <div v-if="visible.hiddenTop > 0 && !showAll" class="msg-fold">
        <HButton variant="ghost" size="sm" @click="showAll = true">
          显示更早的 {{ visible.hiddenTop }} 条消息
        </HButton>
      </div>

      <!-- 消息列表（:key 用 id，流式期间 id 不变，DOM 不重建） -->
      <MessageItem
        v-for="m in visible.items"
        :key="m.id"
        :message="m"
        :is-streaming="m.id === streamingId"
      />

      <!-- 底部折叠提示 -->
      <div v-if="visible.hiddenBottom > 0 && !showAll" class="msg-fold">
        <HButton variant="ghost" size="sm" @click="showAll = true">
          显示更新的 {{ visible.hiddenBottom }} 条消息
        </HButton>
      </div>

      <!-- 加载中：3 个骨架屏 -->
      <div v-if="messages.loading" class="msg-loading-list">
        <HSkeleton shape="block" height="72px" />
        <HSkeleton shape="block" height="120px" />
        <HSkeleton shape="block" height="56px" />
      </div>

      <!-- 空态 -->
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

      <!-- 底部锚点：HScroll 内部自带 IntersectionObserver 锚点 -->
      <div data-scroll-anchor class="msg-anchor" />
    </div>
  </HScroll>
</template>

<style scoped>
.msg-scroll {
  flex: 1;
  min-height: 0; /* 否则 flex 容器被内容撑开；滚动行为见 chat.css 全局 .msg-scroll */
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
</style>

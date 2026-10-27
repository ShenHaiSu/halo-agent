<!-- src/Web.Client/src/components/base/HScroll.vue -->
<script setup lang="ts">
import { onBeforeUnmount, onMounted, ref } from 'vue';
import HaloIcon from '@/components/icons/HaloIcon.vue';

/**
 * 滚动容器（带渐隐提示与"回到底部"按钮）。
 *
 * ============ 这个组件解决三个真实问题 ============
 *  ① 内容被裁切时用户不知道下面还有（渐隐提示）
 *  ② 滚到中间时想回去要一直拖（回到底部按钮）
 *  ③ 流式输出时如果用户已滚到底部，新内容要自动跟随；
 *     如果用户手动往上翻，就不能抢他的滚动位置
 * ============ 自动跟随的实现 ============
 *  用 IntersectionObserver 观察底部锚点：
 *    - 锚点在视口内 → 用户看到了底部 → autoFollow = true
 *    - 锚点不在 → 用户翻上去了 → autoFollow = false
 *  然后 watch 内容变化，仅当 autoFollow 时滚到底部。
 *
 *  * 不能用"scrollTop + clientHeight >= scrollHeight - 4"判断：
 *    在软光栅或高 DPI 下会有 1–2px 误差，导致判断失败。
 *    IntersectionObserver 没有这个问题。
 * ============ ============
 */
const props = withDefaults(
  defineProps<{
    /** 内容变化时是否自动跟随底部 */
    autoFollow?: boolean;
    /** 显示"回到底部"按钮的距离阈值（px） */
    showBottomButtonAt?: number;
  }>(),
  { autoFollow: false, showBottomButtonAt: 120 }
);

const containerRef = ref<HTMLElement | null>(null);
const anchorRef = ref<HTMLElement | null>(null);

const atTop = ref(true);
const atBottom = ref(true);
const showBottomButton = ref(false);
let follow = true;
let io: IntersectionObserver | null = null;

function onScroll() {
  const el = containerRef.value;
  if (!el) return;
  atTop.value = el.scrollTop <= 2;
  const distToBottom = el.scrollHeight - el.scrollTop - el.clientHeight;
  atBottom.value = distToBottom <= 2;
  showBottomButton.value = distToBottom > props.showBottomButtonAt;
}

function scrollToBottom(smooth = true) {
  const el = containerRef.value;
  if (!el) return;
  el.scrollTo({
    top: el.scrollHeight,
    behavior: smooth ? 'smooth' : 'auto', // 'auto' 是合法值
  });
}

function scrollToTop(smooth = true) {
  const el = containerRef.value;
  if (!el) return;
  el.scrollTo({ top: 0, behavior: smooth ? 'smooth' : 'auto' });
}

onMounted(() => {
  const el = containerRef.value;
  const anchor = anchorRef.value;
  if (!el) return;

  el.addEventListener('scroll', onScroll, { passive: true });
  onScroll();

  if (anchor && typeof IntersectionObserver !== 'undefined') {
    io = new IntersectionObserver(
      (entries) => {
        const e = entries[0];
        if (!e) return;
        // 锚点在容器视口内 → 用户在底部
        follow = e.isIntersecting;
        if (props.autoFollow && follow) {
          // 立即跟随一次（新内容可能已经加进来了）
          requestAnimationFrame(() => scrollToBottom(false));
        }
      },
      { root: el, threshold: 0 }
    );
    io.observe(anchor);
  }
});

onBeforeUnmount(() => {
  containerRef.value?.removeEventListener('scroll', onScroll);
  io?.disconnect();
  io = null;
});

/** 供父组件调用（如发送新消息后强制滚到底部） */
function forceScrollToBottom() {
  follow = true;
  scrollToBottom(true);
}

defineExpose({ scrollToBottom, scrollToTop, forceScrollToBottom, atBottom, atTop });
</script>

<template>
  <div class="relative flex min-h-0 flex-col">
    <!-- 顶部渐隐 -->
    <div
      class="pointer-events-none absolute inset-x-0 top-0 z-10 h-4 bg-gradient-to-b from-bg to-transparent transition-opacity duration-fast"
      :class="atTop ? 'opacity-0' : 'opacity-100'"
      aria-hidden="true"
    />

    <div
      ref="containerRef"
      class="min-h-0 flex-1 overflow-y-auto overscroll-contain"
      @scroll.passive="onScroll"
    >
      <slot />
      <!-- 底部锚点 -->
      <div ref="anchorRef" class="h-px w-full" aria-hidden="true" />
    </div>

    <!-- 底部渐隐 -->
    <div
      class="pointer-events-none absolute inset-x-0 bottom-0 z-10 h-6 bg-gradient-to-t from-bg to-transparent transition-opacity duration-fast"
      :class="atBottom ? 'opacity-0' : 'opacity-100'"
      aria-hidden="true"
    />

    <!-- 回到底部 -->
    <Transition name="h-fade">
      <button
        v-if="showBottomButton"
        type="button"
        class="absolute bottom-3 left-1/2 z-20 flex h-8 w-8 -translate-x-1/2 items-center justify-center rounded-full border border-border bg-bg-elevated text-fg-muted shadow-md transition-colors duration-fast hover:text-fg hover:border-border-strong focus-visible:outline-none focus-visible:ring-2 focus-visible:ring-accent/40"
        aria-label="回到底部"
        @click="forceScrollToBottom"
      >
        <HaloIcon name="arrow-down" :size="15" />
      </button>
    </Transition>
  </div>
</template>

<style scoped>
.h-fade-enter-active,
.h-fade-leave-active {
  transition: opacity var(--duration-fast) var(--ease-out);
}
.h-fade-enter-from,
.h-fade-leave-to {
  opacity: 0;
}
</style>

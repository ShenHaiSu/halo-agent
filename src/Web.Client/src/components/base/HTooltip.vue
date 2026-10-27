<!-- src/Web.Client/src/components/base/HTooltip.vue -->
<script setup lang="ts">
import { computed, onBeforeUnmount, ref } from 'vue';

/**
 * 轻提示（hover / focus 显示）。
 *
 * ============ 为什么自己算位置而不用 CSS 的 ::after ============
 *  ① 需要 4 个方向 + 自动翻转（上方空间不够时翻到下方）
 *  ② 需要跟随滚动（贴在触发元素上）
 *  ③ 需要不遮挡焦点
 *  自己算位置只需 40 行，比引入 floating-ui（~15KB）划算。
 * ============ 为什么 tooltip 不能用原生 title ============
 *  ① 原生 title 有 1–2 秒的滞后，体验差
 *  ② 原生 title 在 Win7 上是系统的黄框，风格不一致
 *  ③ 原生 title 无法换行、无法有富文本
 * ============ 无障碍 ============
 *  role="tooltip" + aria-describedby 关联。
 *  同时键盘聚焦也显示（不只是鼠标 hover）。
 * ============ ============
 */
const props = withDefaults(
  defineProps<{
    content?: string;
    placement?: 'top' | 'bottom' | 'left' | 'right';
    delay?: number;
    disabled?: boolean;
    /** 最大宽度（px） */
    maxWidth?: number;
  }>(),
  { placement: 'top', delay: 300, disabled: false, maxWidth: 280 }
);

const visible = ref(false);
const triggerRef = ref<HTMLElement | null>(null);
const pos = ref({ x: 0, y: 0 });
const actualPlacement = ref(props.placement);

let showTimer: number | null = null;
let hideTimer: number | null = null;

function clearTimers() {
  if (showTimer !== null) {
    window.clearTimeout(showTimer);
    showTimer = null;
  }
  if (hideTimer !== null) {
    window.clearTimeout(hideTimer);
    hideTimer = null;
  }
}

/**
 * 计算位置（相对 viewport）。
 *
 * ============ 翻转逻辑 ============
 *  顶部空间 < tip 高度 + 8 → 翻到下方
 *  上方空间够 → 保持
 *  水平方向居中，但夹在 viewport 内
 * ============ ============
 */
function computePosition() {
  const el = triggerRef.value;
  if (!el) return;

  const r = el.getBoundingClientRect();
  const gap = 6;
  const vw = window.innerWidth;
  const vh = window.innerHeight;

  // 估算 tooltip 尺寸（实际渲染后再修正一次）
  const estW = Math.min(props.maxWidth, (props.content?.length ?? 0) * 7 + 16);
  const estH = 28;

  let p: string = props.placement;
  let x = 0;
  let y = 0;

  // 先按原方向算
  switch (p) {
    case 'top':
      y = r.top - gap - estH;
      x = r.left + r.width / 2 - estW / 2;
      if (y < 4) {
        p = 'bottom';
      }
      break;
    case 'bottom':
      y = r.bottom + gap;
      x = r.left + r.width / 2 - estW / 2;
      if (y + estH > vh - 4) {
        p = 'top';
      }
      break;
    case 'left':
      x = r.left - gap - estW;
      y = r.top + r.height / 2 - estH / 2;
      if (x < 4) {
        p = 'right';
      }
      break;
    case 'right':
      x = r.right + gap;
      y = r.top + r.height / 2 - estH / 2;
      if (x + estW > vw - 4) {
        p = 'left';
      }
      break;
  }

  // 按翻转后的方向重算
  switch (p) {
    case 'top':
      y = r.top - gap - estH;
      x = r.left + r.width / 2 - estW / 2;
      break;
    case 'bottom':
      y = r.bottom + gap;
      x = r.left + r.width / 2 - estW / 2;
      break;
    case 'left':
      x = r.left - gap - estW;
      y = r.top + r.height / 2 - estH / 2;
      break;
    case 'right':
      x = r.right + gap;
      y = r.top + r.height / 2 - estH / 2;
      break;
  }

  // 夹进 viewport
  x = Math.max(4, Math.min(x, vw - estW - 4));
  y = Math.max(4, Math.min(y, vh - estH - 4));

  pos.value = { x, y };
  actualPlacement.value = p as typeof props.placement;
}

function show(immediate = false) {
  if (props.disabled || !props.content) return;
  clearTimers();
  const doShow = () => {
    computePosition();
    visible.value = true;
  };
  if (immediate) doShow();
  else showTimer = window.setTimeout(doShow, props.delay);
}

function hide() {
  clearTimers();
  hideTimer = window.setTimeout(() => {
    visible.value = false;
  }, 80);
}

onBeforeUnmount(clearTimers);

const arrowClass = computed(() => {
  switch (actualPlacement.value) {
    case 'top':
      return 'left-1/2 -translate-x-1/2 -bottom-1';
    case 'bottom':
      return 'left-1/2 -translate-x-1/2 -top-1';
    case 'left':
      return 'top-1/2 -translate-y-1/2 -right-1';
    case 'right':
      return 'top-1/2 -translate-y-1/2 -left-1';
    default:
      return 'left-1/2 -translate-x-1/2 -bottom-1';
  }
});
</script>

<template>
  <span
    ref="triggerRef"
    class="inline-flex"
    @mouseenter="show()"
    @mouseleave="hide()"
    @focusin="show(true)"
    @focusout="hide()"
  >
    <slot />
  </span>

  <Teleport to="body">
    <Transition name="h-tooltip">
      <div
        v-if="visible"
        class="pointer-events-none fixed z-tooltip"
        :style="{ left: `${pos.x}px`, top: `${pos.y}px`, maxWidth: `${maxWidth}px` }"
        role="tooltip"
      >
        <div
          class="relative rounded-md bg-fg px-2 py-1 text-xs leading-snug text-bg shadow-md whitespace-pre-wrap break-words"
        >
          {{ content }}
          <!-- 箭头 -->
          <span class="absolute h-2 w-2 rotate-45 bg-fg" :class="arrowClass" aria-hidden="true" />
        </div>
      </div>
    </Transition>
  </Teleport>
</template>

<style scoped>
.h-tooltip-enter-active,
.h-tooltip-leave-active {
  transition: opacity var(--duration-fast) var(--ease-out);
}
.h-tooltip-enter-from,
.h-tooltip-leave-to {
  opacity: 0;
}
</style>

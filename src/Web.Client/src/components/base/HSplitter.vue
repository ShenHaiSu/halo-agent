<!-- src/Web.Client/src/components/base/HSplitter.vue -->
<script setup lang="ts">
import { computed, onBeforeUnmount, ref } from 'vue';

/**
 * 可拖拽分隔条（垂直，用于左右分栏）。
 *
 * ============ 为什么用 PointerEvent 而不是 MouseEvent ============
 *  PointerEvent 统一了鼠标/触屏/笔，且 setPointerCapture 能让
 *  指针移出元素后仍持续接收事件（拖出窗口再拖回来不会中断）。
 * ============ 命中区域（hitArea）技巧 ============
 *  视觉宽度只有 1px（细线），但命中区域 9px（宽 4px 的透明 padding）。
 *  这样既不占视觉空间，又好点中。
 * ============ 双击复位 ============
 *  双击分隔条回到默认宽度。这是编辑器类应用的通用约定。
 * ============ ============
 */
const props = withDefaults(
  defineProps<{
    value: number;
    min?: number;
    max?: number;
    /** left: 分隔条右边的面板宽度（拖左 → 面板变窄）；right: 反之 */
    side?: 'left' | 'right';
    /** 双击复位的宽度 */
    defaultValue?: number;
  }>(),
  { min: 120, max: 800, side: 'left', defaultValue: 260 }
);

const emit = defineEmits<{
  'update:value': [number];
  dragstart: [];
  dragend: [];
}>();

const el = ref<HTMLElement | null>(null);
const active = ref(false);

let startX = 0;
let startValue = 0;
let pointerId = -1;

const clamp = (v: number) => Math.min(props.max, Math.max(props.min, v));

function onPointerDown(e: PointerEvent) {
  if (e.button !== 0) return;
  e.preventDefault();

  active.value = true;
  startX = e.clientX;
  startValue = props.value;
  pointerId = e.pointerId;

  el.value?.setPointerCapture?.(e.pointerId);
  emit('dragstart');
}

function onPointerMove(e: PointerEvent) {
  if (!active.value || e.pointerId !== pointerId) return;
  const dx = e.clientX - startX;
  // side=left：分隔条左边是侧栏，向右拖 = 侧栏变宽
  const delta = props.side === 'left' ? dx : -dx;
  emit('update:value', clamp(startValue + delta));
}

function onPointerUp(_e: PointerEvent) {
  if (!active.value) return;
  active.value = false;
  el.value?.releasePointerCapture?.(pointerId);
  pointerId = -1;
  emit('dragend');
}

function onDoubleClick() {
  emit('update:value', clamp(props.defaultValue));
}

/** 键盘可调整（无障碍：分隔条必须能用方向键调） */
function onKeydown(e: KeyboardEvent) {
  const step = e.shiftKey ? 32 : 8;
  let delta = 0;
  if (e.key === 'ArrowLeft') delta = props.side === 'left' ? -step : step;
  else if (e.key === 'ArrowRight') delta = props.side === 'left' ? step : -step;
  else if (e.key === 'Home') delta = props.min - props.value;
  else if (e.key === 'End') delta = props.max - props.value;
  else return;

  e.preventDefault();
  emit('update:value', clamp(props.value + delta));
}

onBeforeUnmount(() => {
  if (active.value) emit('dragend');
});

const hitClass = computed(() =>
  active.value ? 'bg-accent/60' : 'bg-transparent hover:bg-accent/30 group-hover:bg-border-strong'
);
</script>

<template>
  <div
    ref="el"
    class="group relative z-10 -mx-1 flex w-[9px] shrink-0 cursor-col-resize items-stretch justify-center py-0 select-none"
    role="separator"
    aria-orientation="vertical"
    :aria-valuenow="value"
    :aria-valuemin="min"
    :aria-valuemax="max"
    tabindex="0"
    @pointerdown="onPointerDown"
    @pointermove="onPointerMove"
    @pointerup="onPointerUp"
    @pointercancel="onPointerUp"
    @dblclick="onDoubleClick"
    @keydown="onKeydown"
  >
    <div class="w-px transition-colors duration-fast ease-out" :class="hitClass" />
  </div>
</template>

<!-- src/Web.Client/src/components/base/HPopover.vue -->
<script setup lang="ts">
import { nextTick, onBeforeUnmount, ref, watch } from 'vue';
import { onClickOutside } from '@/composables/useClickOutside';

/**
 * 弹出面板（点击触发，可交互内容）。
 *
 * ============ 与 HTooltip 的区别 ============
 *  | | Tooltip | Popover |
 *  |---|---|---|
 *  | 触发 | hover / focus | click |
 *  | 内容 | 纯文本 | 任意（按钮、输入框、列表） |
 *  | 可交互 | 否（pointer-events: none） | 是 |
 *  | 关闭 | 移出即关 | 点击外部 / ESC |
 * ============ ESC 与焦点 ============
 *  ① ESC 关闭并把焦点还给触发元素
 *  ② 打开时不强制把焦点移进面板（用户可能只是想看一眼）
 * ============ ============
 */
const props = withDefaults(
  defineProps<{
    open?: boolean;
    placement?: 'top-start' | 'top' | 'top-end' | 'bottom-start' | 'bottom' | 'bottom-end';
    disabled?: boolean;
  }>(),
  { open: false, placement: 'bottom-start', disabled: false }
);

const emit = defineEmits<{ 'update:open': [boolean] }>();

const triggerRef = ref<HTMLElement | null>(null);
const panelRef = ref<HTMLElement | null>(null);
const pos = ref({ x: 0, y: 0 });
const actualPlacement = ref(props.placement);

function computePosition() {
  const trigger = triggerRef.value;
  const panel = panelRef.value;
  if (!trigger || !panel) return;

  const r = trigger.getBoundingClientRect();
  const pr = panel.getBoundingClientRect();
  const gap = 4;
  const vw = window.innerWidth;
  const vh = window.innerHeight;

  const [v, h] = props.placement.split('-') as ['top' | 'bottom', 'start' | 'end' | undefined];

  let x = 0;
  let y = 0;
  let vDir = v;
  let hDir = h ?? 'center';

  // 垂直方向
  if (v === 'bottom' && r.bottom + gap + pr.height > vh - 4) vDir = 'top';
  else if (v === 'top' && r.top - gap - pr.height < 4) vDir = 'bottom';

  y = vDir === 'bottom' ? r.bottom + gap : r.top - gap - pr.height;

  // 水平方向
  if (hDir === 'start') x = r.left;
  else if (hDir === 'end') x = r.right - pr.width;
  else x = r.left + r.width / 2 - pr.width / 2;

  // 夹进 viewport
  if (x + pr.width > vw - 4) {
    x = vw - pr.width - 4;
    hDir = 'end';
  }
  if (x < 4) {
    x = 4;
    hDir = 'start';
  }

  pos.value = { x, y };
  actualPlacement.value = `${vDir}${hDir === 'center' ? '' : '-' + hDir}` as typeof props.placement;
}

function toggle() {
  if (props.disabled) return;
  emit('update:open', !props.open);
}

function close() {
  emit('update:open', false);
  triggerRef.value?.focus();
}

watch(
  () => props.open,
  async (v) => {
    if (v) {
      await nextTick();
      computePosition();
      // 渲染完再修正一次（尺寸估算误差）
      requestAnimationFrame(computePosition);
    }
  }
);

onClickOutside(
  panelRef,
  () => {
    if (props.open) close();
  },
  { ignore: [triggerRef] }
);

function onKeydown(e: KeyboardEvent) {
  if (e.key === 'Escape' && props.open) {
    e.preventDefault();
    e.stopPropagation();
    close();
  }
}

onBeforeUnmount(() => {
  /* onClickOutside 内部已清理 */
});
</script>

<template>
  <div class="inline-flex">
    <div ref="triggerRef" class="inline-flex" @click="toggle" @keydown="onKeydown">
      <slot name="trigger" />
    </div>

    <Teleport to="body">
      <Transition name="h-popover">
        <div
          v-if="open"
          ref="panelRef"
          class="fixed z-popover rounded-lg border border-border bg-bg-elevated shadow-lg"
          :style="{ left: `${pos.x}px`, top: `${pos.y}px` }"
          @keydown="onKeydown"
        >
          <slot />
        </div>
      </Transition>
    </Teleport>
  </div>
</template>

<style scoped>
.h-popover-enter-active,
.h-popover-leave-active {
  transition:
    opacity var(--duration-fast) var(--ease-out),
    transform var(--duration-fast) var(--ease-out);
  transform-origin: top center;
}
.h-popover-enter-from,
.h-popover-leave-to {
  opacity: 0;
  transform: scale(0.97) translateY(-2px);
}
</style>

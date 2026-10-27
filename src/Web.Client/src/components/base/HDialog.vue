<!-- src/Web.Client/src/components/base/HDialog.vue -->
<script setup lang="ts">
import { onBeforeUnmount, ref, watch, nextTick } from 'vue';
import HaloIcon from '@/components/icons/HaloIcon.vue';

/**
 * 模态对话框。
 *
 * ============ 必须做对的四件事 ============
 *  ① 焦点陷阱：Tab 不能跑到背景里
 *  ② 打开时聚焦到第一个可聚焦元素（或容器本身）
 *  ③ 关闭时把焦点还给触发元素
 *  ④ ESC 关闭（除非 persistent）
 * ============ 为什么不用原生 <dialog> ============
 *  原生 <dialog> 在 Chrome 109 上可用，但：
 *    ① showModal() 的焦点陷阱在嵌套场景有 bug
 *    ② ::backdrop 的样式在 Win7 上可能与 backdrop-filter 冲突
 *    ③ 关闭动画无法控制（close() 立即移除）
 *  自己实现虽然多 100 行，但完全可控。
 * ============ ============
 */
const props = withDefaults(
  defineProps<{
    open: boolean;
    title?: string;
    /** 点击遮罩/按 ESC 不关闭 */
    persistent?: boolean;
    /** 宽度 */
    width?: 'sm' | 'md' | 'lg' | 'full';
  }>(),
  { persistent: false, width: 'md' }
);

const emit = defineEmits<{ 'update:open': [boolean]; close: [] }>();

const panelRef = ref<HTMLElement | null>(null);
let lastFocused: HTMLElement | null = null;

const widths: Record<string, string> = {
  sm: 'max-w-sm',
  md: 'max-w-lg',
  lg: 'max-w-2xl',
  full: 'max-w-[calc(100vw-64px)]',
};

function close() {
  emit('update:open', false);
  emit('close');
}

function onBackdropClick() {
  if (props.persistent) return;
  close();
}

function onKeydown(e: KeyboardEvent) {
  if (e.key === 'Escape' && !props.persistent) {
    e.preventDefault();
    e.stopPropagation();
    close();
    return;
  }
  if (e.key !== 'Tab') return;

  // 焦点陷阱
  const panel = panelRef.value;
  if (!panel) return;
  const focusables = panel.querySelectorAll<HTMLElement>(
    'a[href], button:not([disabled]), input:not([disabled]), select:not([disabled]), ' +
      'textarea:not([disabled]), [tabindex]:not([tabindex="-1"])'
  );
  if (focusables.length === 0) {
    e.preventDefault();
    panel.focus();
    return;
  }
  const first = focusables[0]!;
  const last = focusables[focusables.length - 1]!;
  const active = document.activeElement as HTMLElement | null;

  if (e.shiftKey && (active === first || active === panel)) {
    e.preventDefault();
    last.focus();
  } else if (!e.shiftKey && active === last) {
    e.preventDefault();
    first.focus();
  }
}

watch(
  () => props.open,
  async (open) => {
    if (open) {
      lastFocused = document.activeElement as HTMLElement | null;
      document.body.style.overflow = 'hidden';
      await nextTick();
      const panel = panelRef.value;
      if (panel) {
        const target = panel.querySelector<HTMLElement>(
          'input, textarea, select, button:not([disabled])'
        );
        (target ?? panel).focus();
      }
    } else {
      document.body.style.overflow = '';
      lastFocused?.focus?.();
      lastFocused = null;
    }
  },
  { immediate: true }
);

onBeforeUnmount(() => {
  document.body.style.overflow = '';
});
</script>

<template>
  <Teleport to="body">
    <Transition name="h-dialog">
      <div
        v-if="open"
        class="fixed inset-0 z-modal flex items-center justify-center p-6"
        role="presentation"
        @keydown="onKeydown"
      >
        <!-- 遮罩 -->
        <div class="absolute inset-0 bg-bg-overlay" aria-hidden="true" @click="onBackdropClick" />

        <!-- 面板 -->
        <div
          ref="panelRef"
          role="dialog"
          aria-modal="true"
          :aria-label="title"
          tabindex="-1"
          :class="[
            'relative w-full rounded-xl bg-bg-elevated shadow-xl',
            'border border-border',
            widths[width],
          ]"
        >
          <!-- 头部 -->
          <div
            v-if="title || $slots.header"
            class="flex items-center justify-between gap-3 px-5 py-3.5 border-b border-border-subtle"
          >
            <slot name="header">
              <h2 class="text-lg font-semibold text-fg u-truncate">{{ title }}</h2>
            </slot>
            <button
              type="button"
              class="shrink-0 rounded p-1 text-fg-subtle hover:bg-bg-muted hover:text-fg focus-visible:outline-none focus-visible:ring-2 focus-visible:ring-accent/40"
              aria-label="关闭"
              @click="close"
            >
              <HaloIcon name="x" :size="16" />
            </button>
          </div>

          <!-- 内容 -->
          <div class="px-5 py-4 text-base text-fg">
            <slot />
          </div>

          <!-- 底部 -->
          <div
            v-if="$slots.footer"
            class="flex items-center justify-end gap-2 px-5 py-3 border-t border-border-subtle"
          >
            <slot name="footer" />
          </div>
        </div>
      </div>
    </Transition>
  </Teleport>
</template>

<style scoped>
.h-dialog-enter-active,
.h-dialog-leave-active {
  transition: opacity var(--duration-base) var(--ease-out);
}
.h-dialog-enter-active > div:last-child,
.h-dialog-leave-active > div:last-child {
  transition:
    transform var(--duration-base) var(--ease-out),
    opacity var(--duration-base) var(--ease-out);
}
.h-dialog-enter-from,
.h-dialog-leave-to {
  opacity: 0;
}
.h-dialog-enter-from > div:last-child,
.h-dialog-leave-to > div:last-child {
  transform: scale(0.97) translateY(4px);
  opacity: 0;
}
</style>

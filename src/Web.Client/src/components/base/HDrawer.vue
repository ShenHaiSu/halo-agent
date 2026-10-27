<!-- src/Web.Client/src/components/base/HDrawer.vue -->
<script setup lang="ts">
import { computed, nextTick, onBeforeUnmount, ref, watch } from 'vue';
import HaloIcon from '@/components/icons/HaloIcon.vue';

/**
 * 侧边抽屉。
 *
 * ============ 与 HDialog 的共用逻辑 ============
 *  焦点陷阱、ESC 关闭、焦点归还、body 滚动锁定——
 *  这四件事的实现在 HDialog 里，但复制一份（约 60 行）比
 *  抽一个 useModal 更可控：抽屉的 DOM 结构不同，
 *  强行共用会让两边都变复杂。
 *
 *  ============ 注意：这是有意的重复 ============
 *  如果将来第三个"模态类"组件出现（比如全屏遮罩），
 *  那时才值得抽 useModal。
 * ============ ============
 */
const props = withDefaults(
  defineProps<{
    open: boolean;
    title?: string;
    side?: 'left' | 'right' | 'top' | 'bottom';
    size?: 'sm' | 'md' | 'lg';
    persistent?: boolean;
  }>(),
  { side: 'right', size: 'md', persistent: false }
);

const emit = defineEmits<{ 'update:open': [boolean]; close: [] }>();

const panelRef = ref<HTMLElement | null>(null);
let lastFocused: HTMLElement | null = null;

const sizes = {
  sm: { horizontal: 'w-72', vertical: 'h-56' },
  md: { horizontal: 'w-96', vertical: 'h-80' },
  lg: { horizontal: 'w-[32rem]', vertical: 'h-[28rem]' },
} as const;

const panelClass = computed(() => {
  const s = sizes[props.size];
  const horizontal = props.side === 'left' || props.side === 'right';
  return [
    'fixed z-modal flex flex-col bg-bg-elevated shadow-xl border-border',
    horizontal ? s.horizontal : `w-full ${s.vertical}`,
    props.side === 'left' ? 'left-0 top-0 h-full border-r' : '',
    props.side === 'right' ? 'right-0 top-0 h-full border-l' : '',
    props.side === 'top' ? 'left-0 top-0 w-full border-b' : '',
    props.side === 'bottom' ? 'left-0 bottom-0 w-full border-t' : '',
  ];
});

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
    <Transition :name="`h-drawer-${side}`">
      <div v-if="open" class="fixed inset-0 z-modal" @keydown="onKeydown">
        <div class="absolute inset-0 bg-bg-overlay" aria-hidden="true" @click="onBackdropClick" />

        <div
          ref="panelRef"
          role="dialog"
          aria-modal="true"
          :aria-label="title"
          tabindex="-1"
          :class="panelClass"
        >
          <div
            v-if="title || $slots.header"
            class="flex shrink-0 items-center justify-between gap-3 border-b border-border-subtle px-4 py-3"
          >
            <slot name="header">
              <h2 class="text-base font-semibold text-fg u-truncate">{{ title }}</h2>
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

          <div class="min-h-0 flex-1 overflow-y-auto px-4 py-3 text-sm text-fg">
            <slot />
          </div>

          <div
            v-if="$slots.footer"
            class="flex shrink-0 items-center justify-end gap-2 border-t border-border-subtle px-4 py-3"
          >
            <slot name="footer" />
          </div>
        </div>
      </div>
    </Transition>
  </Teleport>
</template>

<style scoped>
/* ---------- right ---------- */
.h-drawer-right-enter-active,
.h-drawer-right-leave-active,
.h-drawer-left-enter-active,
.h-drawer-left-leave-active,
.h-drawer-top-enter-active,
.h-drawer-top-leave-active,
.h-drawer-bottom-enter-active,
.h-drawer-bottom-leave-active {
  transition: opacity var(--duration-base) var(--ease-out);
}
.h-drawer-right-enter-active > div:last-child,
.h-drawer-right-leave-active > div:last-child,
.h-drawer-left-enter-active > div:last-child,
.h-drawer-left-leave-active > div:last-child,
.h-drawer-top-enter-active > div:last-child,
.h-drawer-top-leave-active > div:last-child,
.h-drawer-bottom-enter-active > div:last-child,
.h-drawer-bottom-leave-active > div:last-child {
  transition: transform var(--duration-slow) var(--ease-out);
}

.h-drawer-right-enter-from,
.h-drawer-right-leave-to,
.h-drawer-left-enter-from,
.h-drawer-left-leave-to,
.h-drawer-top-enter-from,
.h-drawer-top-leave-to,
.h-drawer-bottom-enter-from,
.h-drawer-bottom-leave-to {
  opacity: 0;
}

.h-drawer-right-enter-from > div:last-child,
.h-drawer-right-leave-to > div:last-child {
  transform: translateX(100%);
}
.h-drawer-left-enter-from > div:last-child,
.h-drawer-left-leave-to > div:last-child {
  transform: translateX(-100%);
}
.h-drawer-top-enter-from > div:last-child,
.h-drawer-top-leave-to > div:last-child {
  transform: translateY(-100%);
}
.h-drawer-bottom-enter-from > div:last-child,
.h-drawer-bottom-leave-to > div:last-child {
  transform: translateY(100%);
}
</style>

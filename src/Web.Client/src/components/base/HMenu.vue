<!-- src/Web.Client/src/components/base/HMenu.vue -->
<script setup lang="ts">
import { nextTick, onBeforeUnmount, ref, watch } from 'vue';
import { onClickOutside } from '@/composables/useClickOutside';

/**
 * 下拉菜单。
 *
 * ============ 键盘导航必须实现（无障碍底线） ============
 *  ↓ / ↑          在项之间移动
 *  Home / End     跳到首 / 尾
 *  Enter / Space  激活
 *  Esc            关闭并归还焦点
 *  Tab            关闭（离开菜单）
 *  首字母          跳到以该字母开头的项
 *
 *  这些是 WAI-ARIA Authoring Practices 对 menu 的要求。
 *  不做键盘导航的"菜单"对键盘用户是完全不可用的。
 * ============ 为什么自己实现而不用 HPopover ============
 *  Menu 需要"焦点在菜单内部游走"，而 Popover 不抢焦点。
 *  两者语义不同，共用会让逻辑混乱。
 * ============ ============
 */
const props = withDefaults(
  defineProps<{
    open?: boolean;
    /** 对齐方式 */
    align?: 'start' | 'end';
    /** 最小宽度（px） */
    minWidth?: number;
  }>(),
  { open: false, align: 'start', minWidth: 160 }
);

const emit = defineEmits<{ 'update:open': [boolean] }>();

const triggerRef = ref<HTMLElement | null>(null);
const menuRef = ref<HTMLElement | null>(null);

function items(): HTMLElement[] {
  const menu = menuRef.value;
  if (!menu) return [];
  return Array.from(
    menu.querySelectorAll<HTMLElement>('[role="menuitem"]:not([aria-disabled="true"])')
  );
}

function focusItem(index: number) {
  const list = items();
  if (list.length === 0) return;
  const i = ((index % list.length) + list.length) % list.length;
  list[i]!.focus();
}

function currentIndex(): number {
  const active = document.activeElement as HTMLElement | null;
  if (!active) return -1;
  return items().indexOf(active);
}

function close(restoreFocus = true) {
  emit('update:open', false);
  if (restoreFocus) triggerRef.value?.focus();
}

function toggle() {
  emit('update:open', !props.open);
}

function onMenuKeydown(e: KeyboardEvent) {
  const idx = currentIndex();

  switch (e.key) {
    case 'ArrowDown':
      e.preventDefault();
      focusItem(idx + 1);
      break;
    case 'ArrowUp':
      e.preventDefault();
      focusItem(idx < 0 ? -1 : idx - 1);
      break;
    case 'Home':
      e.preventDefault();
      focusItem(0);
      break;
    case 'End':
      e.preventDefault();
      focusItem(-1);
      break;
    case 'Escape':
      e.preventDefault();
      e.stopPropagation();
      close();
      break;
    case 'Tab':
      close(false);
      break;
    case 'Enter':
    case ' ': {
      // 让浏览器处理激活；但空格可能会滚动页面
      if (e.key === ' ') e.preventDefault();
      const active = document.activeElement as HTMLElement | null;
      if (active && items().includes(active)) active.click();
      break;
    }
    default: {
      // 首字母跳转
      if (e.key.length === 1 && /[\w\u4e00-\u9fa5]/.test(e.key)) {
        const list = items();
        const start = idx + 1;
        for (let k = 0; k < list.length; k++) {
          const item = list[(start + k) % list.length]!;
          const text = (item.textContent ?? '').trim();
          if (text.toLowerCase().startsWith(e.key.toLowerCase())) {
            e.preventDefault();
            item.focus();
            return;
          }
        }
      }
    }
  }
}

/** 打开时聚焦第一项 */
watch(
  () => props.open,
  async (v) => {
    if (v) {
      await nextTick();
      requestAnimationFrame(() => focusItem(0));
    }
  }
);

onClickOutside(
  menuRef,
  () => {
    if (props.open) close(false);
  },
  { ignore: [triggerRef] }
);

onBeforeUnmount(() => {
  /* 已清理 */
});
</script>

<template>
  <div class="inline-flex">
    <div ref="triggerRef" class="inline-flex" @click="toggle">
      <slot name="trigger" />
    </div>

    <Teleport to="body">
      <Transition name="h-menu">
        <div
          v-if="open"
          ref="menuRef"
          role="menu"
          tabindex="-1"
          class="fixed z-dropdown rounded-lg border border-border bg-bg-elevated py-1 shadow-lg"
          :style="{ minWidth: `${minWidth}px` }"
          @keydown="onMenuKeydown"
        >
          <slot />
        </div>
      </Transition>
    </Teleport>
  </div>
</template>

<style scoped>
.h-menu-enter-active,
.h-menu-leave-active {
  transition:
    opacity var(--duration-fast) var(--ease-out),
    transform var(--duration-fast) var(--ease-out);
  transform-origin: top left;
}
.h-menu-enter-from,
.h-menu-leave-to {
  opacity: 0;
  transform: scale(0.97) translateY(-2px);
}
</style>

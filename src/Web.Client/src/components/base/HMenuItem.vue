<!-- src/Web.Client/src/components/base/HMenuItem.vue -->
<script setup lang="ts">
import HaloIcon from '@/components/icons/HaloIcon.vue';
import type { IconName } from '@/components/icons/paths';

/**
 * 菜单项。
 *
 * ============ 为什么用 role="menuitem" + tabindex=-1 ============
 *  WAI-ARIA 的 menu 模式要求：
 *    ① 菜单容器 role="menu"
 *    ② 每项 role="menuitem"
 *    ③ 项之间用方向键切换，**Tab 不进入项**（Tab 直接离开菜单）
 *  所以每项 tabindex=-1，由 HMenu 程序化聚焦。
 * ============ ============
 */
const props = withDefaults(
  defineProps<{
    icon?: IconName;
    disabled?: boolean;
    /** 危险项（删除等） */
    danger?: boolean;
    /** 右侧快捷键提示 */
    shortcut?: string;
  }>(),
  { disabled: false, danger: false }
);

const emit = defineEmits<{ click: [] }>();

function onClick() {
  if (props.disabled) return;
  emit('click');
}
</script>

<template>
  <button
    type="button"
    role="menuitem"
    tabindex="-1"
    :aria-disabled="disabled ? 'true' : undefined"
    :disabled="disabled"
    class="flex w-full items-center gap-2 px-3 py-1.5 text-left text-sm transition-colors duration-fast focus:outline-none focus-visible:outline-none disabled:cursor-not-allowed disabled:opacity-40"
    :class="
      danger
        ? 'text-danger hover:bg-danger-subtle focus:bg-danger-subtle'
        : 'text-fg hover:bg-bg-muted focus:bg-bg-muted'
    "
    @click="onClick"
  >
    <HaloIcon v-if="icon" :name="icon" :size="14" class="shrink-0 opacity-80" />
    <span v-else class="w-3.5 shrink-0" />

    <span class="min-w-0 flex-1 u-truncate"><slot /></span>

    <span v-if="shortcut" class="shrink-0 font-mono text-2xs text-fg-subtle">{{ shortcut }}</span>
  </button>
</template>

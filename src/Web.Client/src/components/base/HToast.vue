<!-- src/Web.Client/src/components/base/HToast.vue -->
<script setup lang="ts">
import { useToast, type ToastItem } from '@/composables/useToast';
import HaloIcon from '@/components/icons/HaloIcon.vue';
import type { IconName } from '@/components/icons/paths';

/**
 * 全局 Toast 容器（挂一次在 App.vue 里）。
 *
 * ============ 为什么只挂一次 ============
 *  toast 是全局的。多处挂载会出现重复显示。
 *  所以在 App.vue 里挂一个，其他地方只调 useToast()。
 * ============ 位置 ============
 *  右上角（避免遮挡左下角的输入框）。
 *  堆叠从上往下，最新的在最上方。
 * ============ ============
 */
const { toasts, dismiss } = useToast();

const iconMap: Record<ToastItem['level'], IconName> = {
  info: 'info',
  success: 'check-circle',
  warning: 'alert',
  error: 'alert-circle',
};

const styleMap: Record<ToastItem['level'], string> = {
  info: 'border-info/40 bg-info-subtle text-info',
  success: 'border-success/40 bg-success-subtle text-success',
  warning: 'border-warning/40 bg-warning-subtle text-warning',
  error: 'border-danger/40 bg-danger-subtle text-danger',
};
</script>

<template>
  <Teleport to="body">
    <div
      class="pointer-events-none fixed right-4 top-4 z-toast flex w-80 flex-col gap-2"
      aria-live="polite"
      aria-atomic="false"
    >
      <TransitionGroup name="h-toast">
        <div
          v-for="t in toasts"
          :key="t.id"
          class="pointer-events-auto flex items-start gap-2 rounded-lg border px-3 py-2 shadow-lg"
          :class="styleMap[t.level]"
          role="status"
        >
          <HaloIcon :name="iconMap[t.level]" :size="16" class="mt-0.5 shrink-0" />

          <div class="min-w-0 flex-1">
            <p class="text-sm leading-snug break-words">{{ t.text }}</p>
            <button
              v-if="t.action"
              type="button"
              class="mt-1 text-xs font-medium underline underline-offset-2 hover:no-underline"
              @click="t.action.onClick()"
            >
              {{ t.action.label }}
            </button>
          </div>

          <button
            type="button"
            class="shrink-0 rounded p-0.5 opacity-60 hover:opacity-100 focus-visible:outline-none focus-visible:ring-2 focus-visible:ring-current"
            aria-label="关闭提示"
            @click="dismiss(t.id)"
          >
            <HaloIcon name="x" :size="12" />
          </button>
        </div>
      </TransitionGroup>
    </div>
  </Teleport>
</template>

<style scoped>
.h-toast-enter-active,
.h-toast-leave-active {
  transition:
    opacity var(--duration-base) var(--ease-out),
    transform var(--duration-base) var(--ease-out);
}
.h-toast-enter-from {
  opacity: 0;
  transform: translateX(16px);
}
.h-toast-leave-to {
  opacity: 0;
  transform: translateX(16px);
}
.h-toast-move {
  transition: transform var(--duration-base) var(--ease-out);
}
</style>

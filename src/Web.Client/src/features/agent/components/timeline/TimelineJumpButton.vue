<template>
  <Transition name="show-jump">
    <button
      v-if="visible"
      type="button"
      class="show-jump-pill"
      data-testid="show-jump"
      title="回到底部"
      aria-label="回到底部"
      @click="emit('jump')"
    >
      <ArrowDownIcon :size="18" />
    </button>
  </Transition>
</template>

<script setup lang="ts">
import ArrowDownIcon from '@shared/ui/icons/ArrowDownIcon.vue';

defineProps<{ visible: boolean }>();

const emit = defineEmits<{ jump: [] }>();
</script>

<style scoped>
/* 回到底部圆钮：absolute 悬浮于滚动容器之外，不占 scrollHeight，避免出现又收回的抖动。 */
.show-jump-pill {
  position: absolute;
  bottom: 16px;
  right: 16px;
  width: 36px;
  height: 36px;
  padding: 0;
  border-radius: 50%;
  display: inline-flex;
  align-items: center;
  justify-content: center;
  background: var(--accent-primary, #3b82f6);
  color: #fff;
  opacity: 0.75;
  border: none;
  cursor: pointer;
  z-index: 6;
  box-shadow: 0 4px 12px rgba(0, 0, 0, 0.25);
  transition:
    opacity 150ms ease-out,
    background-color 150ms ease-out;
}
.show-jump-pill:hover {
  opacity: 1;
}
.show-jump-pill:active {
  opacity: 1;
}
.show-jump-pill:focus-visible {
  opacity: 1;
  outline: 2px solid var(--accent-primary, #3b82f6);
  outline-offset: 2px;
}
html.light .show-jump-pill {
  color: #0f172a;
}
/* 圆钮淡入（与会话切换无关，保留） */
.show-jump-enter-active {
  transition: opacity 150ms ease-out;
}
.show-jump-enter-from {
  opacity: 0;
}
@media (prefers-reduced-motion: reduce) {
  .show-jump-pill,
  .show-jump-enter-active {
    transition: none;
  }
}
</style>

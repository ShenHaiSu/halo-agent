<!-- src/Web.Client/src/components/sidebar/SessionGroupHeader.vue -->
<script setup lang="ts">
/**
 * 静态分隔行。
 *
 * 「是否画上边线」不能用 CSS :first-child 判断 —— 虚拟滚动只渲染 slice 窗口内的行，
 * 滚到列表中间时窗口首行常常就是 header，:first-child 会随裁剪反复命中/失配，
 * 表现为分隔线忽隐忽现、与相邻分组连出两条线或漏线。
 * 所以由调用方传入 first（含义：这是整份列表的第一行），与窗口位置无关。
 */
defineProps<{ label: string; height: number; first?: boolean }>();
</script>
<template>
  <div
    class="group-head"
    :class="{ 'is-first': first }"
    :style="{ height: `${height}px` }"
    role="presentation"
  >
    {{ label }}
  </div>
</template>
<style scoped>
.group-head {
  display: flex;
  align-items: center;
  padding: 0 var(--space-3);
  font-size: var(--text-2xs);
  color: hsl(var(--fg-subtle));
  font-weight: 500;
  letter-spacing: 0.02em;
  box-sizing: border-box;
  border-top: 1px solid hsl(var(--border));
  user-select: none;
}
.group-head.is-first {
  border-top: 0;
}
</style>

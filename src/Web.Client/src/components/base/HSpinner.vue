<!-- src/Web.Client/src/components/base/HSpinner.vue -->
<script setup lang="ts">
import { computed } from 'vue';

/**
 * 加载指示器。
 *
 * ============ 为什么用 SVG 而不是 CSS border 技巧 ============
 *  ① CSS border 的"半个边框"技巧在软光栅下的圆角渲染不完美
 *  ② SVG 的 stroke-dasharray 能做更自然的"缺口旋转"
 *  ③ 尺寸完全可控（border 方案的小尺寸会出现 1px 抖动）
 * ============ 为什么 inherit-color 是默认 ============
 *  spinner 几乎总是嵌在按钮/输入框里，必须跟随父元素文字颜色。
 *  只有独立使用时才需要显式颜色。
 * ============ ============
 */
const props = withDefaults(
  defineProps<{
    size?: 'xs' | 'sm' | 'md' | 'lg';
    inheritColor?: boolean;
  }>(),
  { size: 'sm', inheritColor: true }
);

const px = computed(() => {
  switch (props.size) {
    case 'xs':
      return 12;
    case 'sm':
      return 14;
    case 'md':
      return 18;
    case 'lg':
      return 24;
    default:
      return 14;
  }
});

const strokeWidth = computed(() => (px.value <= 14 ? 2.5 : 2));
</script>

<template>
  <svg
    class="halo-spinner"
    :class="inheritColor ? 'text-current' : 'text-accent'"
    :width="px"
    :height="px"
    viewBox="0 0 24 24"
    fill="none"
    role="status"
    aria-label="加载中"
  >
    <!-- 底盘：很淡的完整圆 -->
    <circle
      cx="12"
      cy="12"
      r="9"
      stroke="currentColor"
      :stroke-width="strokeWidth"
      opacity="0.22"
    />
    <!-- 旋转弧：约 1/4 圆周 -->
    <circle
      cx="12"
      cy="12"
      r="9"
      stroke="currentColor"
      :stroke-width="strokeWidth"
      stroke-linecap="round"
      stroke-dasharray="14 42"
      class="halo-spinner__arc"
    />
  </svg>
</template>

<style scoped>
.halo-spinner {
  display: inline-block;
  flex: none;
}

.halo-spinner__arc {
  /* 用 transform 旋转：走 GPU 合成路径，不触发重绘 */
  transform-origin: 12px 12px;
  animation: halo-spinner-rotate var(--duration-slower) linear infinite;
}

/* 减动效时退化为"呼吸"而不是"旋转"（旋转在静止时看起来像卡住） */
@media (prefers-reduced-motion: reduce) {
  .halo-spinner__arc {
    animation: halo-spinner-pulse 1.2s ease-in-out infinite;
  }
}

@keyframes halo-spinner-rotate {
  to {
    transform: rotate(360deg);
  }
}

@keyframes halo-spinner-pulse {
  0%,
  100% {
    opacity: 0.35;
  }
  50% {
    opacity: 1;
  }
}
</style>

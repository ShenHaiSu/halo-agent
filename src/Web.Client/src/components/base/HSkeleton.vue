<!-- src/Web.Client/src/components/base/HSkeleton.vue -->
<script setup lang="ts">
/**
 * 骨架屏。
 *
 * ============ 为什么需要骨架屏而不是 spinner ============
 *  ① 骨架屏能让用户预见内容的布局，减少"跳变感"
 *  ② spinner 在大块内容加载时显得"什么都没发生"
 *  ③ 消息列表加载历史时，骨架比 spinner 体验好得多
 * ============ 动画只用 opacity ============
 *  呼吸用 opacity 变化，走合成层，不重排不重绘。
 *  用 background-position 动画（扫光）在软光栅下很卡。
 * ============ ============
 */
withDefaults(
  defineProps<{
    shape?: 'line' | 'circle' | 'block';
    width?: string;
    height?: string;
    /** 行数（仅 line 有效） */
    lines?: number;
    animated?: boolean;
  }>(),
  { shape: 'line', lines: 1, animated: true }
);
</script>

<template>
  <div :class="animated ? 'animate-pulse' : ''" aria-hidden="true">
    <template v-if="shape === 'line'">
      <div
        v-for="i in lines"
        :key="i"
        class="mb-2 h-3 rounded bg-bg-muted"
        :style="{
          width: width ?? (i === lines && lines > 1 ? '60%' : '100%'),
        }"
      />
    </template>
    <div
      v-else-if="shape === 'circle'"
      class="rounded-full bg-bg-muted"
      :style="{ width: width ?? '32px', height: height ?? width ?? '32px' }"
    />
    <div
      v-else
      class="rounded-md bg-bg-muted"
      :style="{ width: width ?? '100%', height: height ?? '80px' }"
    />
  </div>
</template>

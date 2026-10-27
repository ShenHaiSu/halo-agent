<!-- src/Web.Client/src/components/icons/HaloIcon.vue -->
<script setup lang="ts">
import { computed } from 'vue';
import { ICONS, type IconName, type IconPath } from './paths';

/**
 * 唯一的图标组件。
 *
 * ============ 设计要点 ============
 *  ① 所有图标共用 24×24 viewBox，通过 size 缩放
 *  ② 颜色走 currentColor（父元素设置 text-* 即可）
 *  ③ stroke 渲染，stroke-width 可通过 prop 调（细线条在大图标里更好看）
 *  ④ 默认 aria-hidden="true"（图标是装饰，语义由父元素承担）
 *  ⑤ 必须显式给 name，否则 TS 报错 → 用错图标名编译期就发现
 * ============ ============
 */
const props = withDefaults(
  defineProps<{
    name: IconName;
    /** 像素尺寸（正方形） */
    size?: number | string;
    strokeWidth?: number;
    /** 无障碍：如果图标独立传达信息，必须给 label */
    label?: string;
  }>(),
  { size: 16, strokeWidth: 2 }
);

const icon = computed((): IconPath => ICONS[props.name]);
const px = computed(() => (typeof props.size === 'number' ? `${props.size}px` : props.size));
</script>

<template>
  <svg
    class="halo-icon"
    :width="px"
    :height="px"
    :viewBox="icon.viewBox ?? '0 0 24 24'"
    :fill="icon.filled ? 'currentColor' : 'none'"
    :stroke="icon.filled ? 'none' : 'currentColor'"
    :stroke-width="icon.filled ? undefined : strokeWidth"
    stroke-linecap="round"
    stroke-linejoin="round"
    :aria-hidden="label ? undefined : true"
    :role="label ? 'img' : undefined"
    :aria-label="label"
  >
    <path v-for="(d, i) in icon.d" :key="i" :d="d" />
  </svg>
</template>

<style scoped>
.halo-icon {
  display: inline-block;
  flex: none;
  vertical-align: -0.125em; /* 与文字基线微调对齐 */
}
</style>

<!-- src/Web.Client/src/components/input/StopButton.vue -->
<script setup lang="ts">
import HButton from '@/components/base/HButton.vue';

defineProps<{
  /** 已发出取消请求，正在等引擎收尾。 */
  cancelling?: boolean;
}>();
defineEmits<{ click: [] }>();
</script>

<template>
  <!--
    取消中态。
    - loading：HButton 自带的 spinner，且会自动 disabled（HButton.vue:50 isDisabled = disabled || loading）
      → 天然防连点，无需额外 disabled 逻辑。
    - 文案「中断中…」：让用户明确知道请求已发出，而不是没响应。
    - aria-label 同步变化：读屏用户能感知状态切换。
  -->
  <HButton
    variant="secondary"
    size="sm"
    :icon="cancelling ? undefined : 'square'"
    :loading="cancelling === true"
    :aria-label="cancelling ? '正在中断生成' : '停止生成'"
    @click="$emit('click')"
    >{{ cancelling ? '中断中…' : '停止' }}</HButton>
</template>

<!-- src/Web.Client/src/components/input/StopButton.vue -->
<script setup lang="ts">
import HButton from '@/components/base/HButton.vue';

defineEmits<{ click: [] }>();
</script>

<template>
  <HButton variant="secondary" size="sm" icon="square" aria-label="停止生成" @click="$emit('click')"
    >停止</HButton
  >
</template>

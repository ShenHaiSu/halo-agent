<template>
  <div class="transcript-history">
    <div v-for="entry in entries" :key="entry.key" :data-entry="entry.key">
      <slot :entry="entry" />
    </div>
  </div>
</template>
<script setup lang="ts">
// KeepAlive + v-show 由父层控制；本组件只渲染 frozen history（引用不变下游全跳过）
defineProps<{ entries: Array<{ key: string }> }>();
</script>

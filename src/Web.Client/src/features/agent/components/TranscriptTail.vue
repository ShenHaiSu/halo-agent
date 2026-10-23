<template>
  <div class="transcript-tail">
    <div v-for="entry in entries" :key="entry.key" :data-entry="entry.key">
      <slot :entry="entry" />
    </div>
    <slot name="live" />
  </div>
</template>
<script setup lang="ts">
// Tail 独活：随流更新；key = message/turn id
defineProps<{ entries: Array<{ key: string }> }>();
</script>

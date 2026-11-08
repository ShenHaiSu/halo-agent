<template>
  <div class="transcript-tail">
    <div v-for="(entry, idx) in entries" :key="entry.key" :data-entry="entry.key">
      <!-- need02-P3/P4：透出 is-last（末轮交接窗 + 轮内 notice 只挂末条，避免旧轮误挂 live）。老调用方忽略新增槽 prop，兼容。 -->
      <slot :entry="entry" :is-last="idx === entries.length - 1" />
    </div>
    <slot name="live" />
  </div>
</template>
<script setup lang="ts">
// Tail 独活：随流更新；key = message/turn id
defineProps<{ entries: Array<{ key: string }> }>();
</script>

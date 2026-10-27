<!-- src/Web.Client/src/components/input/QueueList.vue -->
<script setup lang="ts">
import HaloIcon from '@/components/icons/HaloIcon.vue';
defineProps<{ items: string[] }>();
defineEmits<{ remove: [index: number] }>();
</script>
<template>
  <div class="queue" role="list" aria-label="排队中的消息">
    <div class="queue-head">
      <HaloIcon name="history" :size="13" />
      <span>排队中（{{ items.length }} 条）</span>
    </div>
    <div v-for="(t, i) in items" :key="i" class="queue-item" role="listitem">
      <span class="queue-no">{{ i + 1 }}</span>
      <span class="queue-text" :title="t">{{ t }}</span>
      <button
        class="queue-del"
        type="button"
        :aria-label="`撤回第 ${i + 1} 条`"
        @click="$emit('remove', i)"
      >
        <HaloIcon name="x" :size="13" />
      </button>
    </div>
  </div>
</template>
<style scoped>
.queue {
  border: 1px dashed hsl(var(--border));
  border-radius: var(--radius-sm);
  padding: var(--space-1) var(--space-2);
  background: hsl(var(--bg-subtle));
}
.queue-head {
  display: flex;
  align-items: center;
  gap: var(--space-1);
  font-size: var(--text-2xs);
  color: hsl(var(--fg-muted));
  margin-bottom: 2px;
}
.queue-item {
  display: flex;
  align-items: center;
  gap: var(--space-2);
  min-width: 0;
}
.queue-no {
  font-size: var(--text-2xs);
  color: hsl(var(--fg-subtle));
  font-family: var(--font-mono);
}
.queue-text {
  flex: 1;
  min-width: 0;
  font-size: var(--text-xs);
  color: hsl(var(--fg-muted));
  overflow: hidden;
  text-overflow: ellipsis;
  white-space: nowrap;
}
.queue-del {
  background: transparent;
  border: 0;
  padding: 2px;
  cursor: pointer;
  color: hsl(var(--fg-subtle));
}
.queue-del:hover {
  color: hsl(var(--danger));
}
</style>

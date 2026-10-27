<!-- src/Web.Client/src/components/assets/AssetListItem.vue -->
<!-- 资源通用列表项（05 §3.3）。 -->
<script setup lang="ts">
defineProps<{
  active: boolean;
  title: string;
  subtitle?: string;
  badge?: string;
  badgeVariant?: 'neutral' | 'accent' | 'warning';
}>();

defineEmits<{ select: [] }>();
</script>

<template>
  <button
    class="ali"
    :class="{ active }"
    type="button"
    :aria-current="active ? 'true' : undefined"
    @click="$emit('select')"
  >
    <span class="ali-body">
      <span class="ali-title">{{ title }}</span>
      <span v-if="subtitle" class="ali-sub">{{ subtitle }}</span>
    </span>
    <span v-if="badge" class="ali-badge" :class="`bv-${badgeVariant ?? 'neutral'}`">{{
      badge
    }}</span>
  </button>
</template>

<style scoped>
.ali {
  display: flex;
  align-items: center;
  gap: var(--space-2);
  width: 100%;
  padding: var(--space-2);
  background: transparent;
  border: 0;
  border-left: 2px solid transparent;
  cursor: pointer;
  text-align: left;
  min-width: 0;
}
.ali:hover {
  background: hsl(var(--bg-subtle));
}
.ali.active {
  background: hsl(var(--bg-muted));
  border-left-color: hsl(var(--accent));
}
.ali-body {
  flex: 1;
  min-width: 0;
  display: flex;
  flex-direction: column;
  gap: 1px;
}
.ali-title {
  font-size: var(--text-sm);
  color: hsl(var(--fg));
  overflow: hidden;
  text-overflow: ellipsis;
  white-space: nowrap;
}
.ali-sub {
  font-size: var(--text-2xs);
  color: hsl(var(--fg-subtle));
  overflow: hidden;
  text-overflow: ellipsis;
  white-space: nowrap;
}
.ali-badge {
  flex: none;
  font-size: var(--text-2xs);
  padding: 0 4px;
  border-radius: var(--radius-xs);
}
.bv-neutral {
  background: hsl(var(--bg-muted));
  color: hsl(var(--fg-muted));
}
.bv-accent {
  background: hsl(var(--accent-subtle));
  color: hsl(var(--accent));
}
.bv-warning {
  background: hsl(var(--warning-subtle));
  color: hsl(var(--warning));
}
</style>

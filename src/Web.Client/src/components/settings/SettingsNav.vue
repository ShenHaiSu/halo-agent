<!-- src/Web.Client/src/components/settings/SettingsNav.vue -->
<!-- 设置页左侧导航：7 分页。 -->
<script setup lang="ts">
import HaloIcon from '@/components/icons/HaloIcon.vue';
import type { IconName } from '@/components/icons/paths';
import type { SettingsTab } from './SettingsApp.vue';

export interface SettingsNavItem {
  key: SettingsTab;
  label: string;
  icon: IconName;
}

defineProps<{
  items: SettingsNavItem[];
  active: SettingsTab;
}>();

defineEmits<{ select: [tab: SettingsTab] }>();
</script>

<template>
  <nav class="snav" aria-label="设置分页">
    <button
      v-for="it in items"
      :key="it.key"
      type="button"
      class="snav-item"
      :class="{ active: active === it.key }"
      :aria-current="active === it.key ? 'page' : undefined"
      @click="$emit('select', it.key)"
    >
      <HaloIcon :name="it.icon" :size="15" />
      <span>{{ it.label }}</span>
    </button>
  </nav>
</template>

<style scoped>
 .snav {
   display: flex;
   flex-direction: column;
   gap: var(--space-0-5);
  min-width: 0;
}
.snav-item {
  display: flex;
  align-items: center;
  gap: var(--space-2);
  padding: var(--space-2) var(--space-3);
  border: 0;
  border-radius: var(--radius-sm);
  background: transparent;
  color: hsl(var(--fg-muted));
  font-size: var(--text-sm);
  cursor: pointer;
  text-align: left;
  white-space: nowrap;
}
.snav-item:hover {
  background: hsl(var(--bg-subtle));
  color: hsl(var(--fg));
}
.snav-item.active {
  background: hsl(var(--bg-muted));
  color: hsl(var(--fg));
  font-weight: 500;
}
 @media (max-width: 900px) { /* P14：与 var(--bp-compact) 同值；var() 不可用于 @media 条件 */
  .snav {
    flex-direction: row;
    overflow-x: auto;
  }
}
</style>

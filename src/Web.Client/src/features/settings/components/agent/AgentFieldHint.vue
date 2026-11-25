<template>
  <n-tooltip :show="visible" trigger="manual" placement="top" :show-arrow="false">
    <template #trigger>
      <span
        class="agent-field-info"
        tabindex="0"
        role="button"
        :aria-label="label"
        @mouseenter="visible = true"
        @mouseleave="visible = false"
        @focus="visible = true"
        @blur="visible = false"
      >
        <Info :size="12" />
      </span>
    </template>
    {{ text }}
  </n-tooltip>
</template>

<script setup lang="ts">
/**
 * naive-ui 的 tooltip.trigger 只接受单一值（hover 与 focus 互斥），
 * 这里用 manual + 显式事件，让鼠标悬停与键盘焦点都能唤出同一份说明。
 */
import { ref } from 'vue';
import { Info } from 'lucide-vue-next';

withDefaults(defineProps<{ text: string; label?: string }>(), { label: '查看说明' });
const visible = ref(false);
</script>

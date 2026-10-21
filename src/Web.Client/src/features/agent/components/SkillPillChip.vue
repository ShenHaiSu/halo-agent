<template>
  <n-popover trigger="hover" placement="top" :show-arrow="false">
    <template #trigger>
      <span
        class="skill-chip"
        :class="scope"
        :data-scope="scope"
        :data-testid="`skill-chip-${scope}-${name}`"
        :aria-label="`技能 ${scope}/${name}`"
        :title="`${scope}/${name}`"
      >
        <span class="skill-chip-scope">{{ scope }}</span>
        <span class="skill-chip-name">{{ name }}</span>
      </span>
    </template>
    <div class="text-xs max-w-64">
      <div class="font-medium">{{ scope }}/{{ name }}</div>
      <div v-if="summary" class="opacity-70 mt-1">{{ summary }}</div>
      <div class="opacity-50 mt-1">发送时将明文插入该技能正文</div>
    </div>
  </n-popover>
</template>
<script setup lang="ts">
/**
 * need22-F04：历史气泡里的 skill chip（展示态）。
 * 纯展示组件：不发请求、不改全局状态（summary 由调用方可选传入）。
 */
defineProps<{
  scope: 'global' | 'project';
  name: string;
  summary?: string;
}>();
</script>

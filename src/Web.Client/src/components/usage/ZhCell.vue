<!-- src/Web.Client/src/components/usage/ZhCell.vue -->
<!-- 数字单元格：值中文紧凑 + title 精确，避免四处散写 title。 -->
<script setup lang="ts">
import { formatNumber, formatZhCount } from '@/utils/format';

defineProps<{
  value: number;
}>();
</script>

<template>
  <td class="px-2 py-1.5 text-right" :title="formatNumber(value)">{{ formatZhCount(value) }}</td>
</template>

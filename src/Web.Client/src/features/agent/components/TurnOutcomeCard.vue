<template>
  <div v-if="visible" class="turn-outcome" role="status">
    <div class="turn-outcome-title">需要注意</div>
    <div class="turn-outcome-body">
      {{ text }}
    </div>
    <div class="turn-outcome-meta">
      {{ stepLabel }}
    </div>
    <button type="button" class="turn-outcome-continue" @click="emit('continue')">继续</button>
  </div>
</template>
<script setup lang="ts">
import { computed } from 'vue';

// TurnOutcomeCard: 仅失败时渲染；readingWindow 内不渲染（父层 v-if 门控）
const props = defineProps<{
  failed: boolean;
  text: string;
  steps: number;
  readingWindow?: boolean;
}>();
const emit = defineEmits<{ continue: [] }>();

const visible = computed(() => props.failed && !props.readingWindow && (props.text || '').trim().length > 0);
const stepLabel = computed(() => `步骤数 ${props.steps}`);
</script>
<style scoped>
.turn-outcome {
  border: 1px solid var(--n-border-color, #f0a);
  border-radius: 8px;
  padding: 10px 12px;
  font-size: 12px;
}
.turn-outcome-title {
  font-weight: 600;
}
</style>

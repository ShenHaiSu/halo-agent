<template>
  <div v-if="visible" class="action-dock" data-testid="composer-action-dock">
    <!-- Permission：风险等级 + 排队数 + 子代理来源 + prompt/args；拒绝(ghost)/允许本会话(次)/允许一次(主) -->
    <div v-if="permission" class="action-card" data-testid="permission-card">
      <div class="action-title">
        需要审批 · {{ permission.risk }} <span v-if="permission.queue > 0">· 排队 {{ permission.queue }}</span>
      </div>
      <div v-if="permission.subLabel" class="action-sub">子代理：{{ permission.subLabel }}</div>
      <div class="action-body">
        {{ permission.prompt }}
      </div>
      <div class="action-row">
        <button type="button" :disabled="resolving" @click="emit('deny')">拒绝</button>
        <button type="button" :disabled="resolving" @click="emit('allowSession')">允许本会话</button>
        <button type="button" :disabled="resolving" @click="emit('allowOnce')">允许一次</button>
      </div>
    </div>
    <!-- AskTool：多问向导 -->
    <div v-if="ask" class="action-card" data-testid="ask-card">
      <div class="action-title">
        提问 {{ ask.current }}/{{ ask.total }} <span v-if="ask.queued > 0">· 排队 {{ ask.queued }}</span>
      </div>
      <div class="action-body">
        {{ ask.question }}
      </div>
      <div class="action-row">
        <button type="button" :disabled="resolving" @click="emit('askSkip')">Skip</button>
        <button type="button" :disabled="resolving" @click="emit('askSubmit')">Next</button>
      </div>
    </div>
    <!-- PlanApproval：pending 才出动作行 -->
    <div v-if="plan" class="action-card" data-testid="plan-card" :aria-busy="resolving ? 'true' : undefined">
      <div class="action-title">
        {{ plan.kind === 'goal' ? '目标确认' : '计划审批' }}
      </div>
      <div class="action-row">
        <button type="button" :disabled="resolving" @click="emit('planDeny')">拒绝</button>
        <button type="button" :disabled="resolving" @click="emit('planApprove')">批准</button>
      </div>
    </div>
  </div>
</template>
<script setup lang="ts">
import { computed } from 'vue';

// 全部 live-only：只挂 composer dock，永不进 transcript；readingWindow 全禁（父层门控）
const props = defineProps<{
  readingWindow?: boolean;
  resolving?: boolean;
  permission?: { risk: string; queue: number; subLabel?: string; prompt: string } | null;
  ask?: { question: string; current: number; total: number; queued: number } | null;
  plan?: { kind: 'plan' | 'goal' } | null;
}>();
const emit = defineEmits<{
  deny: [];
  allowSession: [];
  allowOnce: [];
  askSkip: [];
  askSubmit: [];
  planDeny: [];
  planApprove: [];
}>();

const visible = computed(() => !props.readingWindow && (!!props.permission || !!props.ask || !!props.plan));
</script>
<style scoped>
.action-dock {
  display: flex;
  flex-direction: column;
  gap: 8px;
}
.action-card {
  border: 1px solid var(--n-border-color, #ddd);
  border-radius: 8px;
  padding: 10px 12px;
  font-size: 12px;
}
.action-title {
  font-weight: 600;
}
.action-row {
  display: flex;
  gap: 8px;
  margin-top: 8px;
}
</style>

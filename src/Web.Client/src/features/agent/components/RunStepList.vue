<template>
  <div class="space-y-2 min-w-0" data-testid="turn-run-steps">
    <div v-for="(group, i) in groups" :key="group.key" class="min-w-0" :data-testid="`turn-run-step-${i + 1}`">
      <div class="flex items-center gap-1.5 text-[11px] text-[var(--reasoning-text)] select-none">
        <span class="font-mono shrink-0 opacity-70">{{ i + 1 }}</span>
        <span class="truncate">第 {{ i + 1 }} 步</span>
        <span v-if="reasoningOf(group)" class="shrink-0 opacity-60">· 思考</span>
        <span v-if="(group.toolMessages?.length || 0) > 0" class="shrink-0 font-mono opacity-60"
          >· {{ group.toolMessages.length }} 个工具</span
        >
      </div>
      <div
        v-if="reasoningOf(group)"
        class="mt-1 whitespace-pre-wrap break-words select-text app-text-small text-[var(--reasoning-text)] max-h-[40vh] overflow-y-auto reasoning-seg"
      >
        {{ reasoningOf(group) }}
      </div>
      <div v-if="(group.toolMessages?.length || 0) > 0" class="mt-1 space-y-1.5 min-w-0">
        <ToolCallCard v-for="t in group.toolMessages || []" :key="toolKey(t)" :card="cardFor(t, group)" :default-collapsed="true" />
      </div>
    </div>
  </div>
</template>
<script setup lang="ts">
/**
 * need-01 P2：合并舱的「按 iteration 分节」明细列表。
 *
 * 抽出来的目的：活动轨与稳态共用同一渲染器（Phase 3 复用），避免两套 DOM 语义；
 * 也让 TurnRunItem 只负责外壳（汇总行 + 折叠过渡），明细渲染可独立快照。
 */
import type { UnifiedChatMessage } from '@shared/bridge/hostBridge';
import type { TurnGroup } from '@features/session/utils/turnGroup';
import type { AgentToolCard } from '@features/agent/stores/model';
import ToolCallCard from './ToolCallCard.vue';

defineProps<{
  groups: TurnGroup[];
  cardFor: (m: UnifiedChatMessage, g: TurnGroup) => AgentToolCard;
  toolKey: (m: UnifiedChatMessage) => string;
}>();

function reasoningOf(g: TurnGroup): string {
  return String(g.reasoning || '');
}
</script>
<style scoped>
/* 与 StreamSegItem 的 reasoning-seg 保持一致：滚动条出现时宽度不变 */
.reasoning-seg {
  scrollbar-gutter: stable;
  overflow-anchor: auto;
}
</style>

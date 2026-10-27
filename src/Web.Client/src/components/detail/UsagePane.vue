<!-- src/Web.Client/src/components/detail/UsagePane.vue -->
<!-- 用量分页：输入 / 输出 tokens（usage prompt/completion/dropped）。 -->
<script setup lang="ts">
import { computed } from 'vue';
import { useSessionStore } from '@/stores/session';
import { formatNumber } from '@/utils/format';

const session = useSessionStore();

const input = computed(() => session.usage.input);
const output = computed(() => session.usage.output);
const total = computed(() => input.value + output.value);
</script>

<template>
  <div class="pane">
    <div class="urow">
      <span class="ulabel">输入 Tokens</span>
      <span class="uvalue font-mono">{{ formatNumber(input) }}</span>
    </div>
    <div class="urow">
      <span class="ulabel">输出 Tokens</span>
      <span class="uvalue font-mono">{{ formatNumber(output) }}</span>
    </div>
    <div class="urow total">
      <span class="ulabel">合计</span>
      <span class="uvalue font-mono">{{ formatNumber(total) }}</span>
    </div>
    <p class="uhint">用量随会话累计。开启新会话后重新计数。</p>
  </div>
</template>

<style scoped>
.pane {
  padding: var(--space-3);
  display: flex;
  flex-direction: column;
  gap: var(--space-2);
  overflow-y: auto;
}
.urow {
  display: flex;
  align-items: center;
  justify-content: space-between;
  gap: var(--space-2);
  font-size: var(--text-sm);
}
.ulabel {
  color: hsl(var(--fg-muted));
}
.uvalue {
  color: hsl(var(--fg));
}
.total .ulabel,
.total .uvalue {
  font-weight: 600;
}
.uhint {
  margin: var(--space-1) 0 0;
  font-size: var(--text-2xs);
  color: hsl(var(--fg-subtle));
}
</style>

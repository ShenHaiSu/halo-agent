<!-- src/Web.Client/src/components/detail/UsagePane.vue -->
<!-- 右抽屉本会话用量（usage.summary({sessionId}) 本会话合计；llmCalls 读 DB 落盘值，与内存事件值取 max）。 -->
<script setup lang="ts">
import { computed, onMounted } from 'vue';
import { useMessagesStore } from '@/stores/messages';
import { useSessionStore } from '@/stores/session';
import { useSessionUsage } from '@/composables/useSessionUsage';
import { formatHitRate } from '@/components/usage/usageTypes';
import { formatNumber } from '@/utils/format';
import HButton from '@/components/base/HButton.vue';

const session = useSessionStore();
const messages = useMessagesStore();
const { summary, failed, degraded, refresh } = useSessionUsage();

const input = computed(() => summary.value?.input ?? 0);
const cached = computed(() => summary.value?.cached ?? 0);
const output = computed(() => summary.value?.output ?? 0);
const total = computed(() => summary.value?.total ?? input.value + output.value);
const hitRateText = computed(() => formatHitRate(input.value, summary.value?.hitRate ?? 0));
const turns = computed(() => messages.turns.length);
const requests = computed(() => summary.value?.requests ?? 0);
// DB 落盘滞后 turn 一拍（ordering），同进程内内存事件值更新；取 max 避免回退跳变；老引擎缺字段按 0。
const llmCalls = computed(() => Math.max(summary.value?.llmCalls ?? 0, session.llmCalls));

const isEmpty = computed(
  () =>
    !failed.value &&
    input.value === 0 &&
    cached.value === 0 &&
    output.value === 0 &&
    llmCalls.value === 0 &&
    turns.value === 0
);

onMounted(() => {
  void refresh();
});
</script>

<template>
  <div class="pane">
    <template v-if="failed">
      <p class="uerror">汇总加载失败，稍后重试</p>
      <div><HButton variant="secondary" size="sm" @click="refresh">重试</HButton></div>
      <div class="urow">
        <span class="ulabel">网络请求（LLM调用）</span>
        <span class="uvalue font-mono">{{ formatNumber(llmCalls) }}</span>
      </div>
    </template>
    <template v-else-if="isEmpty">
      <p class="uempty">本会话暂无用量，完成一次对话后统计</p>
      <p class="uhint">
        水位为最近一次请求的瞬时上下文占用，用量合计为本会话落盘累计，两者口径不同，不直接对齐。
      </p>
      <p class="uhint">
        LLM调用为本会话落盘累计，重启/切回历史会话后仍保留；中断/零用量回合的调用次数不计入。
      </p>
      <p class="uhint">
        回合数为界面可见回合；用量记录为有用量落盘的回合数，中断/零用量回合会计入前者但不计入后者。
      </p>
    </template>
    <template v-else>
      <div v-if="degraded" class="udegraded">
        本地库不可用 <span class="umemok">内存态可用</span>
      </div>
      <div class="urow" :class="{ dim: degraded }">
        <span class="ulabel">输入 Tokens</span>
        <span class="uvalue font-mono">{{ formatNumber(input) }}</span>
      </div>
      <div class="urow" :class="{ dim: degraded }">
        <span class="ulabel">命中缓存</span>
        <span class="uvalue font-mono">{{ formatNumber(cached) }}</span>
      </div>
      <div class="urow" :class="{ dim: degraded }">
        <span class="ulabel">输出 Tokens</span>
        <span class="uvalue font-mono">{{ formatNumber(output) }}</span>
      </div>
      <div class="urow total" :class="{ dim: degraded }">
        <span class="ulabel">合计</span>
        <span class="uvalue font-mono">{{ formatNumber(total) }}</span>
      </div>
      <div class="urow" :class="{ dim: degraded }">
        <span class="ulabel">命中率</span>
        <span class="uvalue font-mono">{{ hitRateText }}</span>
      </div>
      <div class="urow">
        <span class="ulabel">当前会话回合</span>
        <span class="uvalue font-mono">{{ formatNumber(turns) }}</span>
      </div>
      <div class="urow" :class="{ dim: degraded }">
        <span class="ulabel">用量记录</span>
        <span class="uvalue font-mono">{{ formatNumber(requests) }}</span>
      </div>
      <div class="urow">
        <span class="ulabel">网络请求（LLM调用）</span>
        <span class="uvalue font-mono">{{ formatNumber(llmCalls) }}</span>
      </div>
      <p class="uhint">
        水位为最近一次请求的瞬时上下文占用，用量合计为本会话落盘累计，两者口径不同，不直接对齐。
      </p>
      <p class="uhint">
        LLM调用为本会话落盘累计，重启/切回历史会话后仍保留；中断/零用量回合的调用次数不计入。
      </p>
      <p class="uhint">
        回合数为界面可见回合；用量记录为有用量落盘的回合数，中断/零用量回合会计入前者但不计入后者。
      </p>
    </template>
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
.urow.dim {
  opacity: 0.5;
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
.uempty {
  font-size: var(--text-sm);
  color: hsl(var(--fg-muted));
}
.uerror {
  font-size: var(--text-sm);
  color: hsl(var(--danger));
}
.udegraded {
  font-size: var(--text-2xs);
  color: hsl(var(--fg-subtle));
}
.umemok {
  margin-left: var(--space-1);
}
</style>

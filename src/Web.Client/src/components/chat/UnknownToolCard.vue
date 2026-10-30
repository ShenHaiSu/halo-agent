<!-- src/Web.Client/src/components/chat/UnknownToolCard.vue -->
<!-- P13 responses 原生工具卡：ToolCard 按 kind==='unknown' 路由到此。
     标题 rawType ?? name；正文 args rawJSON + output（payload 本次置 null 不展示）；
     折叠样式与 status/durationMs 来源同 ToolCardBody（缺省不展示）。样式见 styles/chat.css（P13 节）。 -->
<script setup lang="ts">
import { computed, ref } from 'vue';
import type { ToolCallInfo } from '@/stores/messages';
import { deriveState } from './toolMeta';
import ToolOutput from './ToolOutput.vue';
import HaloIcon from '@/components/icons/HaloIcon.vue';
import HBadge from '@/components/base/HBadge.vue';

const props = defineProps<{ tool: ToolCallInfo }>();

const title = computed(() => props.tool.rawType ?? props.tool.name);
const open = ref(false);

/** 参数：终版 args 美化 JSON（失败则原文），与 ToolCardBody 一致（首版不支持渐进 argsDelta）。 */
const prettyArgs = computed(() => {
  try {
    return JSON.stringify(JSON.parse(props.tool.arguments), null, 2);
  } catch {
    return props.tool.arguments;
  }
});

const state = computed(() => deriveState(props.tool, false));

const statusText = computed(() => {
  switch (state.value) {
    case 'pending':
      return '排队中';
    case 'running':
      return '执行中';
    case 'awaiting-user':
      return '等待授权';
    case 'done':
      return '成功';
    case 'error':
      return '失败';
    case 'cancelled':
      return '已中断';
    default:
      return '未知';
  }
});

const badgeVariant = computed(() => {
  switch (state.value) {
    case 'running':
      return 'accent' as const;
    case 'awaiting-user':
      return 'warning' as const;
    case 'done':
      return 'success' as const;
    case 'error':
      return 'danger' as const;
    default:
      return 'default' as const;
  }
});

const durationText = computed(() =>
  typeof props.tool.durationMs === 'number'
    ? props.tool.durationMs >= 1000
      ? `${(props.tool.durationMs / 1000).toFixed(1)}s`
      : `${props.tool.durationMs}ms`
    : ''
);

function toggle(): void {
  open.value = !open.value;
}
</script>

<template>
  <div class="unk-card">
    <button
      class="unk-head"
      type="button"
      :aria-expanded="open"
      :aria-label="`${title}（${statusText}）`"
      @click="toggle"
    >
      <HaloIcon name="info" :size="15" class="unk-icon" />
      <span class="unk-title">{{ title }}</span>
      <span class="unk-spacer" />
      <span v-if="durationText" class="unk-duration">{{ durationText }}</span>
      <HBadge :variant="badgeVariant" :content="statusText" />
      <HaloIcon :name="open ? 'chevron-down' : 'chevron-right'" :size="14" />
    </button>
    <div v-if="open" class="unk-body">
      <section class="unk-section">
        <div class="unk-section-title">原生参数</div>
        <pre class="unk-pre">{{ prettyArgs }}</pre>
      </section>
      <section v-if="tool.output || tool.result" class="unk-section">
        <div class="unk-section-title">{{ tool.ok === false ? '错误' : '输出' }}</div>
        <ToolOutput :output="tool.output ?? tool.result ?? ''" />
      </section>
      <section v-else-if="tool.status === 'pending'" class="unk-pending">等待执行…</section>
    </div>
  </div>
</template>

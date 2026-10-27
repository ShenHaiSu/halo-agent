<!-- src/Web.Client/src/components/detail/ContextPane.vue -->
<!-- 上下文分页：最大轮数 / 工作目录 / 会话 ID / 已用 tokens。 -->
<script setup lang="ts">
import { computed } from 'vue';
import { useSessionStore } from '@/stores/session';
import { useHostStore } from '@/stores/host';
import { formatNumber } from '@/utils/format';

const session = useSessionStore();
const host = useHostStore();

const used = computed(() => session.usage.input + session.usage.output);
const cwd = computed(() => session.cwd || host.systemInfo?.cwd || '（未知）');
</script>

<template>
  <div class="pane">
    <div class="crow">
      <span class="clabel">工作目录</span>
      <span class="cvalue font-mono" :title="cwd">{{ cwd }}</span>
    </div>
    <div class="crow">
      <span class="clabel">会话 ID</span>
      <span class="cvalue font-mono">{{ session.id || '（新会话）' }}</span>
    </div>
    <div class="crow">
      <span class="clabel">最大轮数</span>
      <span class="cvalue font-mono">{{ session.maxTurns }}</span>
    </div>
    <div class="crow">
      <span class="clabel">已用 Tokens</span>
      <span class="cvalue font-mono">{{ formatNumber(used) }}</span>
    </div>
    <div v-if="session.persona" class="crow">
      <span class="clabel">当前人格</span>
      <span class="cvalue">{{ session.persona }}</span>
    </div>
    <p class="chint">上下文不足时可用 /compact 压缩，或在“模型”页调大上下文长度。</p>
    <p class="chint">最大轮数 / 人格仅本次会话生效，重启后恢复默认。如需永久修改，请到“设置 → 模型”。</p>
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
.crow {
  display: flex;
  align-items: baseline;
  justify-content: space-between;
  gap: var(--space-2);
  font-size: var(--text-sm);
  min-width: 0;
}
.clabel {
  color: hsl(var(--fg-muted));
  flex: none;
}
.cvalue {
  color: hsl(var(--fg));
  min-width: 0;
  overflow: hidden;
  text-overflow: ellipsis;
  white-space: nowrap;
  text-align: right;
}
.chint {
  margin: var(--space-1) 0 0;
  font-size: var(--text-2xs);
  color: hsl(var(--fg-subtle));
}
</style>

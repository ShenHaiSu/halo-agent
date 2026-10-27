<!-- src/Web.Client/src/components/detail/McpPane.vue -->
<!-- MCP 分页：tool.mcpStatus 服务列表，未连接红点 + 错误摘要 + 重新连接。 -->
<script setup lang="ts">
import { onMounted, ref } from 'vue';
import { transport } from '@/transport';
import HButton from '@/components/base/HButton.vue';
import HSpinner from '@/components/base/HSpinner.vue';
import HEmpty from '@/components/base/HEmpty.vue';

interface McpServer {
  name: string;
  connected: boolean;
  toolCount: number;
  error?: string;
}

const servers = ref<McpServer[]>([]);
const loading = ref(true);
const error = ref<string | null>(null);

async function load(): Promise<void> {
  loading.value = true;
  error.value = null;
  try {
    const r = (await transport.request('tool.mcpStatus', null)) as { servers?: McpServer[] };
    servers.value = Array.isArray(r.servers) ? r.servers : [];
  } catch (e) {
    error.value = (e as Error).message;
    servers.value = [];
  } finally {
    loading.value = false;
  }
}

onMounted(() => void load());
</script>

<template>
  <div class="pane">
    <div class="pane-head">
      <span class="pane-title">MCP 扩展</span>
      <span class="spacer" />
      <HButton size="sm" variant="ghost" :loading="loading" @click="load">重新连接</HButton>
    </div>

    <div v-if="loading && servers.length === 0" class="state">
      <HSpinner size="sm" /><span>加载中…</span>
    </div>
    <div v-else-if="error" class="state err" role="alert">
      <span>读取失败：{{ error }}</span>
      <HButton size="sm" variant="secondary" @click="load">重试</HButton>
    </div>
    <HEmpty
      v-else-if="servers.length === 0"
      icon="layers"
      title="还没有配置扩展"
      description="在设置页“扩展”中添加 MCP 服务。"
    />
    <ul v-else class="mlist">
      <li v-for="s in servers" :key="s.name" class="mitem">
        <span class="dot" :class="s.connected ? 'ok' : 'bad'" aria-hidden="true" />
        <span class="mname">{{ s.name }}</span>
        <span class="mbadge" :class="s.connected ? 'ok' : 'bad'">
          {{ s.connected ? `${s.toolCount} 个工具` : '未连接' }}
        </span>
        <span v-if="s.error" class="merr" :title="s.error">{{ s.error }}</span>
      </li>
    </ul>
  </div>
</template>

<style scoped>
.pane {
  display: flex;
  flex-direction: column;
  min-height: 0;
  overflow-y: auto;
}
.pane-head {
  display: flex;
  align-items: center;
  gap: var(--space-2);
  padding: var(--space-2) var(--space-3);
  border-bottom: 1px solid hsl(var(--border));
  flex: none;
  position: sticky;
  top: 0;
  background: hsl(var(--bg));
}
.pane-title {
  font-size: var(--text-sm);
  font-weight: 600;
}
.spacer {
  flex: 1;
  min-width: 0;
}
.state {
  display: flex;
  align-items: center;
  gap: var(--space-2);
  padding: var(--space-3);
  font-size: var(--text-xs);
  color: hsl(var(--fg-muted));
}
.state.err {
  color: hsl(var(--danger));
  flex-direction: column;
  align-items: flex-start;
}
.mlist {
  list-style: none;
  margin: 0;
  padding: var(--space-2) var(--space-3);
  display: flex;
  flex-direction: column;
  gap: var(--space-2);
}
.mitem {
  display: flex;
  align-items: center;
  gap: var(--space-2);
  min-width: 0;
  font-size: var(--text-sm);
}
.dot {
  width: 8px;
  height: 8px;
  border-radius: 50%;
  flex: none;
}
.dot.ok {
  background: hsl(var(--success));
}
.dot.bad {
  background: hsl(var(--danger));
}
.mname {
  font-family: var(--font-mono);
  min-width: 0;
  overflow: hidden;
  text-overflow: ellipsis;
  white-space: nowrap;
}
.mbadge {
  flex: none;
  font-size: var(--text-2xs);
  padding: 0 6px;
  border-radius: var(--radius-xs);
}
.mbadge.ok {
  background: hsl(var(--success-subtle));
  color: hsl(var(--success));
}
.mbadge.bad {
  background: hsl(var(--danger-subtle));
  color: hsl(var(--danger));
}
.merr {
  font-size: var(--text-xs);
  color: hsl(var(--danger));
  overflow: hidden;
  text-overflow: ellipsis;
  white-space: nowrap;
  min-width: 0;
}
</style>

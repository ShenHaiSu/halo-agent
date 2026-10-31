<!-- src/Web.Client/src/components/settings/pages/McpPage.vue -->
<!-- MCP 扩展分页：JSON 等宽编辑 + 本地 JSON.parse 校验行号 + 格式化 + 重载 + notes + 状态列表 + 重连。 -->
<script setup lang="ts">
import { computed, onMounted, ref } from 'vue';
import { transport } from '@/transport';
import { useToast } from '@/composables/useToast';
import HTextarea from '@/components/base/HTextarea.vue';
import HButton from '@/components/base/HButton.vue';
import HSpinner from '@/components/base/HSpinner.vue';
import { validateJson } from '../validators';

const toast = useToast();

interface McpServer {
  name: string;
  connected: boolean;
  toolCount: number;
  error?: string;
}

const text = ref('');
const mcpPlaceholder = '{ "mcpServers": { } }';
const original = ref('');
const configPath = ref('');
const notes = ref<Array<{ level: 'info' | 'warn' | 'error'; text: string }>>([]);
const servers = ref<McpServer[]>([]);
const loading = ref(true);
const saving = ref(false);
const reconnecting = ref(false);
const jsonError = ref<string | null>(null);

const dirty = computed(() => text.value !== original.value);

async function refreshStatus(): Promise<void> {
  reconnecting.value = true;
  try {
    const r = (await transport.request('tool.mcpStatus', null)) as { servers?: McpServer[] };
    servers.value = Array.isArray(r.servers) ? r.servers : [];
  } catch {
    servers.value = [];
  } finally {
    reconnecting.value = false;
  }
}

async function load(): Promise<void> {
  loading.value = true;
  notes.value = [];
  try {
    const r = (await transport.request('settings.mcp.get', null)) as {
      path?: string;
      text?: string;
    };
    text.value = typeof r.text === 'string' ? r.text : '';
    original.value = text.value;
    configPath.value = typeof r.path === 'string' ? r.path : '';
    await refreshStatus();
  } catch (e) {
    notes.value = [{ level: 'error', text: (e as Error).message }];
  } finally {
    loading.value = false;
  }
}

function format(): void {
  const v = validateJson(text.value);
  if (!v.ok) {
    jsonError.value =
      (v.line ?? 0) > 1
        ? `无法格式化：JSON 语法错误（第 ${v.line} 行）`
        : `无法格式化：${v.message}`;
    return;
  }
  try {
    text.value = JSON.stringify(
      JSON.parse(text.value === '' ? '{}' : text.value) as unknown,
      null,
      2
    );
    jsonError.value = null;
  } catch (e) {
    jsonError.value = `无法格式化：${(e as Error).message}`;
  }
}

async function save(): Promise<void> {
  const v = validateJson(text.value);
  if (!v.ok) {
    jsonError.value =
      (v.line ?? 0) > 1
        ? `JSON 语法错误（第 ${v.line} 行）：${v.message}`
        : `JSON 语法错误：${v.message}`;
    return;
  }
  jsonError.value = null;
  saving.value = true;
  try {
    const r = (await transport.request('settings.mcp.save', { text: text.value })) as {
      ok?: boolean;
      notes?: string[];
    };
    notes.value = (r.notes ?? []).map((t) => ({ level: 'warn' as const, text: t }));
    if (r.ok === false) {
      toast.error('保存未通过校验（输入已保留）');
      return;
    }
    original.value = text.value;
    toast.success('已保存');
    await refreshStatus();
  } catch (e) {
    toast.error(`保存失败：${(e as Error).message}（输入已保留）`);
  } finally {
    saving.value = false;
  }
}

function isDirty(): boolean {
  return dirty.value;
}
function resetToSaved(): void {
  text.value = original.value;
  jsonError.value = null;
}
defineExpose({ isDirty, resetToSaved });

onMounted(() => void load());
</script>

<template>
  <div class="page">
    <h2 class="page-title">MCP 扩展</h2>
    <p class="page-desc">
      MCP（Model Context Protocol，模型上下文协议）扩展可以为模型提供额外工具。 配置文件：<code>{{
        configPath || '（未读取）'
      }}</code>
    </p>

    <div v-if="loading" class="muted"><HSpinner size="sm" /><span>加载中…</span></div>

    <template v-else>
      <div class="editor-head">
        <HButton size="sm" variant="ghost" @click="format">格式化</HButton>
        <HButton size="sm" variant="ghost" @click="load">从磁盘重新加载</HButton>
        <span class="spacer" />
        <HButton size="sm" variant="primary" :disabled="!dirty" :loading="saving" @click="save"
          >保存</HButton
        >
      </div>

       <HTextarea
         v-model="text"
         class="editor font-mono"
         :rows="16"
         spellcheck="false"
         :placeholder="mcpPlaceholder"
       />

      <div v-if="jsonError" class="err" role="alert">{{ jsonError }}</div>
      <div v-for="(n, i) in notes" :key="i" class="note" :class="n.level" role="status">
        {{ n.text }}
      </div>

      <h3 class="sub-title">连接状态</h3>
      <div class="srv-head">
        <span class="spacer" />
        <HButton size="sm" variant="ghost" :loading="reconnecting" @click="refreshStatus"
          >重新连接</HButton
        >
      </div>
      <div v-if="servers.length === 0" class="muted">还没有配置任何扩展。</div>
      <div v-for="s in servers" :key="s.name" class="srv">
        <span class="dot" :class="s.connected ? 'ok' : 'bad'" aria-hidden="true" />
        <span class="srv-name">{{ s.name }}</span>
        <span class="srv-badge" :class="s.connected ? 'ok' : 'bad'">
          {{ s.connected ? `${s.toolCount} 个工具` : '未连接' }}
        </span>
        <span v-if="s.error" class="srv-err" :title="s.error">{{ s.error }}</span>
      </div>
    </template>
  </div>
</template>

<style scoped>
 .page {
   max-width: var(--settings-page-max, 860px);
 }
 .page-title {
   font-size: var(--text-base);
   font-weight: 600;
   margin: 0 0 var(--space-2);
 }
.page-desc {
  font-size: var(--text-sm);
  color: hsl(var(--fg-muted));
  margin: 0 0 var(--space-3);
  line-height: 1.6;
}
.page-desc code {
  font-family: var(--font-mono);
  font-size: var(--text-xs);
}
 .editor-head {
   display: flex;
   align-items: center;
   gap: var(--space-2);
   margin-bottom: var(--space-1-5);
 }
.spacer {
  flex: 1;
  min-width: 0;
}
.editor {
  font-size: var(--text-sm);
}
.err {
  margin-top: var(--space-2);
  padding: var(--space-2);
  background: hsl(var(--danger-subtle));
  color: hsl(var(--danger));
  border-radius: var(--radius-sm);
  font-size: var(--text-xs);
}
.note {
  margin-top: var(--space-1);
  font-size: var(--text-xs);
  padding: var(--space-1) var(--space-2);
  border-radius: var(--radius-sm);
}
.note.warn {
  background: hsl(var(--warning-subtle));
  color: hsl(var(--warning));
}
.note.error {
  background: hsl(var(--danger-subtle));
  color: hsl(var(--danger));
}
.note.info {
  background: hsl(var(--bg-muted));
  color: hsl(var(--fg-muted));
}
.sub-title {
  font-size: var(--text-sm);
  font-weight: 600;
  margin: var(--space-4) 0 var(--space-2);
}
.srv-head {
  display: flex;
  align-items: center;
  margin-bottom: var(--space-1);
}
.muted {
  display: flex;
  align-items: center;
  gap: var(--space-2);
  color: hsl(var(--fg-muted));
  font-size: var(--text-sm);
}
 .srv {
   display: flex;
   align-items: center;
   gap: var(--space-2);
   padding: var(--space-0-5) 0;
   min-width: 0;
 }
 .dot {
   width: 6px;
   height: 6px;
   border-radius: 50%;
   flex: none;
 }
.dot.ok {
  background: hsl(var(--success));
}
.dot.bad {
  background: hsl(var(--danger));
}
.srv-name {
  font-size: var(--text-sm);
  font-family: var(--font-mono);
}
 .srv-badge {
   flex: none;
   font-size: var(--text-2xs);
   padding: 0 var(--space-1);
   border-radius: var(--radius-xs);
 }
.srv-badge.ok {
  background: hsl(var(--success-subtle));
  color: hsl(var(--success));
}
.srv-badge.bad {
  background: hsl(var(--danger-subtle));
  color: hsl(var(--danger));
}
.srv-err {
  font-size: var(--text-xs);
  color: hsl(var(--danger));
  overflow: hidden;
  text-overflow: ellipsis;
  white-space: nowrap;
  min-width: 0;
}
</style>

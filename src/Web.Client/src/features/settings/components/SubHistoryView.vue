<template>
  <div ref="scrollRef" data-testid="sub-history-view" class="flex-1 min-h-0 overflow-y-auto px-4 py-4 space-y-4">
    <div data-testid="sub-history-header" class="sticky top-0 z-10 rounded-md border border-subtle bg-elevated px-3 py-2 text-xs flex items-center gap-2 flex-wrap">
      <span>子会话 {{ detail?.label || activeLabel }} </span>
      <span data-testid="sub-status-badge" class="font-mono">{{ detail?.status || 'unknown' }}</span>
      <span v-if="detail?.taskPrompt" class="text-secondary truncate max-w-[320px]" :title="detail.taskPrompt">{{ taskShort }} <button data-testid="sub-task-toggle" @click="showFull = !showFull">{{ showFull ? '收起' : '展开' }}</button></span>
      <span v-if="detail" class="font-mono text-secondary">IN {{ detail.inputTokens }} / OUT {{ detail.outputTokens }} / Cache {{ detail.cachedTokens }}</span>
      <n-button size="tiny" data-testid="sub-refresh-btn" @click="onRefresh">刷新</n-button>
      <n-button v-if="isRunning" size="tiny" type="error" ghost data-testid="sub-cancel-btn" @click="onCancel">掐断</n-button>
      <span v-if="detail" class="font-mono text-secondary">IN {{ detail.inputTokens }} / OUT {{ detail.outputTokens }} / Cache {{ detail.cachedTokens }}</span>
      <n-button size="tiny" data-testid="sub-refresh-btn" @click="onRefresh">刷新</n-button>
      <n-button v-if="isRunning" size="tiny" type="error" ghost data-testid="sub-cancel-btn" @click="onCancel">掐断</n-button>
    </div>
    <div v-if="isRunning" class="h-1 rounded bg-surface overflow-hidden"><div class="h-full w-1/3 bg-accent animate-pulse" /></div>
    <div v-if="loading" class="py-10 text-center text-xs text-secondary">加载中…</div>
    <div v-else-if="error" data-testid="sub-error" class="py-10 text-center text-xs">
      <p>{{ error }}</p>
      <n-button size="small" @click="onRefresh">重试</n-button>
      <n-button size="small" @click="onBack">返回主会话</n-button>
    </div>
    <div v-else-if="!messages.length" data-testid="sub-empty" class="py-10 text-center text-xs text-secondary">
      <n-empty description="子尚未输出（running 中…)" />
      <n-button size="small" class="mt-2" @click="onBack">返回主会话</n-button>
    </div>
    <template v-else>
      <div v-for="item in items" :key="item.key" class="space-y-2 min-w-0">
        <div v-if="item.kind === 'user'" class="flex justify-end" :ref="setAnchor(item.key)">
          <div class="max-w-[85%] rounded-lg border border-subtle bg-elevated px-3.5 py-2.5 text-sm whitespace-pre-wrap break-words">{{ item.msg?.content }}</div>
        </div>
        <div v-else-if="item.kind === 'system'" class="text-[11px] text-secondary text-center font-mono px-4" :ref="setAnchor(item.key)">{{ item.msg?.content }}</div>
        <div v-else class="space-y-2" :ref="setAnchor(item.key)">
          <div v-if="item.group?.content" class="text-sm leading-relaxed break-words whitespace-pre-wrap">{{ item.group.content }}</div>
          <div v-if="item.group?.reasoning" class="text-xs text-secondary whitespace-pre-wrap">{{ item.group.reasoning }}</div>
          <div v-for="t in (item.group?.toolMessages || [])" :key="t.id" class="rounded-md border border-subtle bg-surface px-2.5 py-1.5 text-xs font-mono">🔧 {{ t.name || 'tool' }} · {{ (t.content || '').slice(0, 200) }}</div>
        </div>
      </div>
      <div v-if="detail?.errorMessage" class="text-xs text-rose-500">失败原因：{{ detail.errorMessage }}</div>
    </template>
  </div>
</template>
<script setup lang="ts">
import { computed, onMounted, onUnmounted, ref, watch, type ComponentPublicInstance } from 'vue';
import { useSubSessionStore } from '@/stores/subSessionStore';
import { useWorkspaceStore } from '@/stores/workspaceStore';
import { buildTimelineItems } from '@/composables/useTimelineGroups';
import { useTimelineNav } from '@/composables/useTimelineNav';
import { useMessage } from 'naive-ui';

const sub = useSubSessionStore();
const ws = useWorkspaceStore();
const message = useMessage();
const nav = useTimelineNav();
const scrollRef = ref<HTMLElement | null>(null);
const showFull = ref(false);
const detail = computed(() => sub.detail);
const messages = computed(() => sub.messages);
const loading = computed(() => sub.loading);
const error = computed(() => sub.error);
const activeLabel = computed(() => sub.activeSubLabel || sub.activeSubId.slice(-6));
const isRunning = computed(() => detail.value?.status === 'running' || detail.value?.status === 'pending');
const taskShort = computed(() => {
  const t = detail.value?.taskPrompt || '';
  if (showFull.value) return t;
  return t.length > 80 ? t.slice(0, 80) + '…' : t;
});
const items = computed(() => buildTimelineItems(messages.value || []));
function setAnchor(key: string) {
  return (el: Element | ComponentPublicInstance | null) => {
    if (el instanceof HTMLElement) nav.register(key, el);
    else nav.unregister(key);
  };
}
watch(
  () => items.value.map((i) => i.key),
  (keys) => { try { nav.setOrder(keys); } catch { /* ignore */ } },
  { immediate: true }
);
onMounted(() => {
  try {
    nav.setContainer(scrollRef.value);
    nav.setOrder(items.value.map((i) => i.key));
  } catch { /* ignore */ }
});
onUnmounted(() => {
  try { nav.clear(); nav.setContainer(null); } catch { /* ignore */ }
});
async function onRefresh() {
  const wsId = ws.activeWorkspaceId || '';
  await sub.refreshDetail(wsId);
  await sub.refreshMessages(wsId);
}
async function onCancel() {
  const wsId = ws.activeWorkspaceId || '';
  const r = await sub.cancelSub(wsId);
  if (r.ok) message.success('已发送子取消指令');
  else message.error(r.message);
}
function onBack() { sub.exitSub(); }
</script>

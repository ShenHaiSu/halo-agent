<!-- src/Web.Client/src/components/input/ModelSelectorPanel.vue -->
<!-- 模型选择器分组面板：按 Provider 分组 + 搜索 + listbox 键盘（P12-01）。 -->
<script setup lang="ts">
import { computed, nextTick, onMounted, ref, watch } from 'vue';
import HInput from '@/components/base/HInput.vue';
import HButton from '@/components/base/HButton.vue';
import type { ProviderEdit } from '@/components/settings/providerEditing';

const props = defineProps<{
  groups: ProviderEdit[];
  activeProvider: string;
  activeModel: string;
}>();

const emit = defineEmits<{
  select: [providerId: string, modelId: string];
  close: [];
  openSettings: [];
}>();

const query = ref('');
const activeIdx = ref(0);
const listRef = ref<HTMLElement | null>(null);

interface Row {
  kind: 'model';
  providerId: string;
  modelId: string;
  protocol: string;
  disabled: boolean;
  providerDisplay: string;
  modelDisplay: string;
}

const filteredGroups = computed(() => {
  const q = query.value.trim().toLowerCase();
  if (!q) return props.groups;
  return props.groups
    .map((g) => ({
      ...g,
      models: g.models.filter(
        (m) =>
          g.id.toLowerCase().includes(q) ||
          (g.displayName ?? '').toLowerCase().includes(q) ||
          m.id.toLowerCase().includes(q) ||
          (m.displayName ?? '').toLowerCase().includes(q),
      ),
    }))
    .filter(
      (g) =>
        g.id.toLowerCase().includes(q) ||
        (g.displayName ?? '').toLowerCase().includes(q) ||
        g.models.length > 0,
    );
});

/** 扁平可选行（disabled 也占位，保证上下移动连续；Enter 时跳过）。 */
const flatRows = computed<Row[]>(() => {
  const rows: Row[] = [];
  for (const g of filteredGroups.value) {
    for (const m of g.models) {
      rows.push({
        kind: 'model',
        providerId: g.id,
        modelId: m.id,
        protocol: m.protocol ?? 'chat',
        disabled: (m.protocol ?? 'chat') !== 'chat',
        providerDisplay: g.displayName || g.id,
        modelDisplay: m.displayName || m.id,
      });
    }
  }
  return rows;
});

const totalModels = computed(() => props.groups.reduce((n, g) => n + g.models.length, 0));

watch(query, () => {
  activeIdx.value = 0;
});

watch(
  () => props.groups,
  () => {
    if (activeIdx.value >= flatRows.value.length) activeIdx.value = 0;
  },
);

function isActive(pid: string, mid: string): boolean {
  return pid === props.activeProvider && mid === props.activeModel;
}

function formatCtx(n: number | null): string {
  if (n === null || n === undefined) return '';
  if (n >= 1000000) {
    const m = n / 1000000;
    return `${Number.isInteger(m) ? m : m.toFixed(1)}M`;
  }
  if (n >= 1000) return `${Math.round(n / 1000)}K`;
  return String(n);
}

function onKeydown(e: KeyboardEvent): void {
  const rows = flatRows.value;
  switch (e.key) {
    case 'ArrowDown':
      e.preventDefault();
      if (rows.length) activeIdx.value = (activeIdx.value + 1) % rows.length;
      scrollActiveIntoView();
      break;
    case 'ArrowUp':
      e.preventDefault();
      if (rows.length) activeIdx.value = (activeIdx.value - 1 + rows.length) % rows.length;
      scrollActiveIntoView();
      break;
    case 'Home':
      e.preventDefault();
      activeIdx.value = 0;
      scrollActiveIntoView();
      break;
    case 'End':
      e.preventDefault();
      if (rows.length) activeIdx.value = rows.length - 1;
      scrollActiveIntoView();
      break;
    case 'Enter': {
      e.preventDefault();
      const r = rows[activeIdx.value];
      if (r && !r.disabled) emit('select', r.providerId, r.modelId);
      break;
    }
    case 'Escape':
      e.preventDefault();
      e.stopPropagation();
      emit('close');
      break;
    case 'Tab':
      emit('close');
      break;
  }
}

function scrollActiveIntoView(): void {
  void nextTick(() => {
    listRef.value?.querySelector(`[data-idx="${activeIdx.value}"]`)?.scrollIntoView({ block: 'nearest' });
  });
}

function onSearchKeydown(e: KeyboardEvent): void {
  // 搜索框聚焦时上下键仍可切到列表。
  if (e.key === 'ArrowDown' || e.key === 'ArrowUp' || e.key === 'Home' || e.key === 'End' || e.key === 'Enter') {
    onKeydown(e);
  } else if (e.key === 'Escape') {
    e.preventDefault();
    e.stopPropagation();
    emit('close');
  }
}

function rowGlobalIdx(pid: string, mid: string): number {
  return flatRows.value.findIndex((r) => r.providerId === pid && r.modelId === mid);
}

defineExpose({
  focusSearch: () => {
    void nextTick(() => {
      const el = listRef.value?.parentElement?.querySelector('input') as HTMLElement | null;
      el?.focus();
    });
  },
});

onMounted(() => {
  void nextTick(() => {
    const el = listRef.value?.parentElement?.querySelector('input') as HTMLElement | null;
    el?.focus();
  });
});
</script>

<template>
  <div class="ms-panel" role="listbox" aria-label="模型选择" @keydown="onKeydown">
    <div class="ms-search">
      <HInput
        :model-value="query"
        placeholder="搜索 Provider / 模型（大小写不敏感）"
        size="sm"
        @update:model-value="(v: string) => (query = v)"
        @keydown="onSearchKeydown"
      />
    </div>
    <div v-if="groups.length === 0" class="ms-empty" role="status">
      暂无 Provider，去模型设置新建。
      <HButton size="sm" variant="ghost" @click="emit('openSettings')">去模型设置</HButton>
    </div>
    <div v-else-if="totalModels === 0" class="ms-empty" role="status">
      暂无模型，去模型设置添加。
      <HButton size="sm" variant="ghost" @click="emit('openSettings')">去模型设置</HButton>
    </div>
    <div v-else-if="filteredGroups.length === 0" class="ms-empty" role="status">
      无匹配结果。
      <HButton size="sm" variant="ghost" @click="query = ''">清空搜索</HButton>
    </div>
    <div v-else ref="listRef" class="ms-list">
      <div v-for="g in filteredGroups" :key="g.id" role="group" :aria-label="g.displayName || g.id" class="ms-group">
        <div class="ms-grouphead">
          <span class="ms-gname">{{ g.displayName || g.id }}</span>
          <span v-if="g.displayName" class="ms-gid">{{ g.id }}</span>
          <span class="ms-gcount">{{ g.models.length }}</span>
          <span v-if="g.id === activeProvider" class="ms-active-dot" aria-label="使用中">●</span>
        </div>
        <div v-if="g.models.length === 0" class="ms-hint">该 Provider 下暂无模型</div>
        <div
          v-for="m in g.models"
          :key="`${g.id}/${m.id}`"
          :data-idx="rowGlobalIdx(g.id, m.id)"
          role="option"
          :aria-selected="isActive(g.id, m.id)"
          :aria-disabled="(m.protocol ?? 'chat') !== 'chat' || undefined"
          class="ms-row"
          :class="{
            current: isActive(g.id, m.id),
            focused: rowGlobalIdx(g.id, m.id) === activeIdx,
            disabled: (m.protocol ?? 'chat') !== 'chat',
          }"
          :title="(m.protocol ?? 'chat') !== 'chat' ? '即将支持，当前仅 chat 可用' : `${g.id}/${m.id}`"
          @click="(m.protocol ?? 'chat') === 'chat' && emit('select', g.id, m.id)"
          @mouseenter="activeIdx = Math.max(0, rowGlobalIdx(g.id, m.id))"
        >
          <span v-if="isActive(g.id, m.id)" class="ms-cur" aria-hidden="true">●</span>
          <span class="ms-mmain">{{ m.displayName || m.id }}</span>
          <span v-if="m.displayName" class="ms-mid">{{ m.id }}</span>
          <span class="ms-tag">{{ m.protocol ?? 'chat' }}</span>
          <span v-if="m.contextWindow" class="ms-ctx">{{ formatCtx(m.contextWindow) }}</span>
          <span v-if="m.acceptsImages" class="ms-img" aria-label="支持图片">图</span>
        </div>
      </div>
    </div>
    <div class="ms-foot">
      <HButton size="sm" variant="ghost" @click="emit('openSettings')">去模型设置</HButton>
    </div>
  </div>
</template>

<style scoped>
.ms-panel {
  min-width: 320px;
  max-width: 420px;
  max-height: 320px;
  display: flex;
  flex-direction: column;
  background: hsl(var(--bg-elevated));
  border: 1px solid hsl(var(--border));
  border-radius: var(--radius-md);
  box-shadow: var(--shadow-lg);
  overflow: hidden;
}
.ms-search {
  padding: var(--space-2);
  border-bottom: 1px solid hsl(var(--border-subtle));
}
.ms-list {
  overflow-y: auto;
  padding: var(--space-1);
  min-height: 0;
}
.ms-group {
  padding: var(--space-1) 0;
}
.ms-grouphead {
  display: flex;
  align-items: center;
  gap: var(--space-1-5);
  padding: var(--space-1) var(--space-2);
}
.ms-gname {
  font-weight: 700;
  font-size: var(--text-sm);
  color: hsl(var(--fg));
  overflow: hidden;
  text-overflow: ellipsis;
  white-space: nowrap;
}
.ms-gid {
  font-family: var(--font-mono);
  font-size: var(--text-2xs);
  color: hsl(var(--fg-subtle));
  overflow: hidden;
  text-overflow: ellipsis;
  white-space: nowrap;
}
.ms-gcount {
  font-size: var(--text-2xs);
  background: hsl(var(--bg-muted));
  color: hsl(var(--fg-muted));
  padding: 0 var(--space-1);
  border-radius: var(--radius-sm);
}
.ms-active-dot {
  color: hsl(var(--accent));
  font-size: var(--text-xs);
}
.ms-hint {
  font-size: var(--text-xs);
  color: hsl(var(--fg-subtle));
  padding: 0 var(--space-2) var(--space-1);
}
.ms-row {
  display: flex;
  align-items: center;
  gap: var(--space-1-5);
  padding: var(--space-1) var(--space-2);
  border-radius: var(--radius-sm);
  cursor: pointer;
  min-width: 0;
}
.ms-row:hover,
.ms-row.focused {
  background: hsl(var(--bg-muted));
}
.ms-row.current {
  background: hsl(var(--accent-subtle));
}
.ms-row.disabled {
  cursor: not-allowed;
  opacity: 0.55;
}
.ms-cur {
  color: hsl(var(--accent));
  font-size: 10px;
  flex-shrink: 0;
}
.ms-mmain {
  font-size: var(--text-sm);
  color: hsl(var(--fg));
  overflow: hidden;
  text-overflow: ellipsis;
  white-space: nowrap;
}
.ms-mid {
  font-family: var(--font-mono);
  font-size: var(--text-2xs);
  color: hsl(var(--fg-subtle));
  overflow: hidden;
  text-overflow: ellipsis;
  white-space: nowrap;
}
.ms-tag {
  font-size: var(--text-2xs);
  background: hsl(var(--bg-muted));
  color: hsl(var(--fg-muted));
  padding: 0 var(--space-1);
  border-radius: var(--radius-sm);
  flex-shrink: 0;
}
.ms-ctx {
  font-size: var(--text-2xs);
  color: hsl(var(--fg-subtle));
  font-family: var(--font-mono);
  flex-shrink: 0;
}
.ms-img {
  font-size: var(--text-2xs);
  color: hsl(var(--accent));
  flex-shrink: 0;
}
.ms-empty {
  padding: var(--space-3) var(--space-2);
  font-size: var(--text-sm);
  color: hsl(var(--fg-muted));
  display: flex;
  align-items: center;
  gap: var(--space-2);
  flex-wrap: wrap;
}
.ms-foot {
  display: flex;
  justify-content: flex-end;
  padding: var(--space-1-5) var(--space-2);
  border-top: 1px solid hsl(var(--border-subtle));
}
</style>

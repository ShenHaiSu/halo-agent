<!-- src/Web.Client/src/components/settings/fields/ProbeResultDialog.vue -->
<!-- 探测名单 dialog：名单展示 + 搜索过滤 + 勾选加入（P12-03）。基于 HDialog。 -->
<script setup lang="ts">
import { computed, ref, watch } from 'vue';
import HDialog from '@/components/base/HDialog.vue';
import HButton from '@/components/base/HButton.vue';
import HInput from '@/components/base/HInput.vue';
import HCheckbox from '@/components/base/HCheckbox.vue';

const props = withDefaults(
  defineProps<{
    open: boolean;
    models?: string[];
    /** 已在列表的模型 id（精确 Ordinal 匹配，行 disabled）。 */
    existing?: string[];
  }>(),
  { models: () => [], existing: () => [] },
);

const emit = defineEmits<{ 'update:open': [boolean]; close: []; confirm: [names: string[]] }>();

const query = ref('');
const checked = ref<Set<string>>(new Set());

const existingSet = computed(() => new Set(props.existing));
const fresh = computed(() => props.models.filter((m) => !existingSet.value.has(m)));
const existingCount = computed(() => props.models.filter((m) => existingSet.value.has(m)).length);

const filtered = computed(() => {
  const q = query.value.trim().toLowerCase();
  if (!q) return props.models;
  return props.models.filter((m) => m.toLowerCase().includes(q));
});

const freshFiltered = computed(() => filtered.value.filter((m) => !existingSet.value.has(m)));
const selectedCount = computed(() => [...checked.value].filter((m) => !existingSet.value.has(m)).length);

function resetSelection(): void {
  // 默认勾选全部“新增”，已存在默认不勾。
  checked.value = new Set(fresh.value);
}

watch(
  () => [props.open, props.models] as const,
  ([open]) => {
    if (open) {
      query.value = '';
      resetSelection();
    }
  },
  { immediate: true },
);

function toggleOne(name: string, on: boolean): void {
  const next = new Set(checked.value);
  if (on) next.add(name);
  else next.delete(name);
  checked.value = next;
}

function selectAll(): void {
  checked.value = new Set([...checked.value, ...freshFiltered.value]);
}

function selectNewOnly(): void {
  checked.value = new Set(fresh.value);
}

function clearAll(): void {
  checked.value = new Set();
}

function close(): void {
  emit('update:open', false);
  emit('close');
}

function confirm(): void {
  const names = props.models.filter((m) => checked.value.has(m) && !existingSet.value.has(m));
  emit('update:open', false);
  emit('close');
  emit('confirm', names);
}
</script>

<template>
  <HDialog :open="open" title="探测结果" width="md" @close="close">
    <div class="probe-dlg">
      <div class="probe-top">
        <span class="probe-count" role="status">找到 {{ models.length }} 个（{{ existingCount }} 个已在列表）</span>
        <div class="probe-actions">
          <HButton size="sm" variant="ghost" :disabled="freshFiltered.length === 0" @click="selectAll">全选</HButton>
          <HButton size="sm" variant="ghost" :disabled="fresh.length === 0" @click="selectNewOnly">仅选新增</HButton>
          <HButton size="sm" variant="ghost" :disabled="selectedCount === 0" @click="clearAll">清空</HButton>
        </div>
      </div>
      <HInput :model-value="query" placeholder="搜索模型名（大小写不敏感）" size="sm" @update:model-value="(v: string) => (query = v)" />
      <div v-if="models.length === 0" class="probe-empty" role="status">
        远端返回 0 个，请检查 baseUrl / 权限；也可手动添加模型。
      </div>
      <div v-else-if="filtered.length === 0" class="probe-empty" role="status">
        无匹配结果。
        <HButton size="sm" variant="ghost" @click="query = ''">清空搜索</HButton>
      </div>
      <div v-else class="probe-list" role="list">
        <label v-for="m in filtered" :key="m" class="probe-row" :class="{ exists: existingSet.has(m) }">
          <HCheckbox
            :model-value="checked.has(m)"
            :disabled="existingSet.has(m)"
            :aria-label="m"
            @update:model-value="(v: boolean) => toggleOne(m, v)"
          />
          <span class="probe-name" :title="m">{{ m }}</span>
          <span v-if="existingSet.has(m)" class="probe-badge">已存在</span>
        </label>
      </div>
    </div>
    <template #footer>
      <HButton size="sm" variant="ghost" @click="close">取消</HButton>
      <HButton size="sm" variant="primary" :disabled="selectedCount === 0" @click="confirm">
        加入所选 ({{ selectedCount }})
      </HButton>
    </template>
  </HDialog>
</template>

<style scoped>
.probe-dlg {
  display: flex;
  flex-direction: column;
  gap: var(--space-2);
  min-width: 0;
}
.probe-top {
  display: flex;
  align-items: center;
  justify-content: space-between;
  gap: var(--space-2);
  flex-wrap: wrap;
}
.probe-count {
  font-size: var(--text-sm);
  color: hsl(var(--fg-muted));
}
.probe-actions {
  display: flex;
  gap: var(--space-1);
}
.probe-list {
  display: flex;
  flex-direction: column;
  gap: var(--space-1);
  max-height: 320px;
  overflow-y: auto;
  min-width: 0;
}
.probe-row {
  display: flex;
  align-items: center;
  gap: var(--space-2);
  padding: var(--space-1) var(--space-2);
  border-radius: var(--radius-sm);
  cursor: pointer;
  min-width: 0;
}
.probe-row:hover {
  background: hsl(var(--bg-muted));
}
.probe-row.exists {
  cursor: not-allowed;
  opacity: 0.65;
}
.probe-name {
  font-family: var(--font-mono);
  font-size: var(--text-sm);
  overflow: hidden;
  text-overflow: ellipsis;
  white-space: nowrap;
  min-width: 0;
  flex: 1;
}
.probe-badge {
  font-size: var(--text-2xs);
  color: hsl(var(--fg-subtle));
  background: hsl(var(--bg-muted));
  padding: 0 var(--space-1);
  border-radius: var(--radius-sm);
  flex-shrink: 0;
}
.probe-empty {
  font-size: var(--text-sm);
  color: hsl(var(--fg-muted));
  padding: var(--space-3) 0;
  display: flex;
  align-items: center;
  gap: var(--space-2);
}
</style>

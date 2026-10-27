<!-- src/Web.Client/src/components/settings/fields/HeadersEditor.vue -->
<!-- 自定义请求头键值表格：增删改 + key 校验（validateHeaderName）。 -->
<script setup lang="ts">
import { computed } from 'vue';
import HInput from '@/components/base/HInput.vue';
import HButton from '@/components/base/HButton.vue';
import HIconButton from '@/components/base/HIconButton.vue';
import { validateHeaderName } from '../validators';

export interface HeaderRow {
  key: string;
  value: string;
}

const props = defineProps<{
  modelValue: HeaderRow[];
}>();

const emit = defineEmits<{ 'update:modelValue': [rows: HeaderRow[]] }>();

function set(rows: HeaderRow[]): void {
  emit('update:modelValue', rows);
}

function addRow(): void {
  set([...props.modelValue, { key: '', value: '' }]);
}

function removeRow(i: number): void {
  set(props.modelValue.filter((_, k) => k !== i));
}

function setKey(i: number, v: string): void {
  set(props.modelValue.map((r, k) => (k === i ? { key: v, value: r.value } : r)));
}

function setValue(i: number, v: string): void {
  set(props.modelValue.map((r, k) => (k === i ? { key: r.key, value: v } : r)));
}

/** 行 key 是否非法（空行不算错，保存时忽略）。 */
function keyError(row: HeaderRow): string | null {
  if (!row.key.trim()) return null;
  return validateHeaderName(row.key.trim()) ? null : '请求头名称不合法';
}

const hasError = computed(() => props.modelValue.some((r) => keyError(r) !== null));

defineExpose({ hasError });
</script>

<template>
  <div class="headers">
    <div v-if="modelValue.length === 0" class="headers-empty">暂无自定义请求头。</div>
    <div v-for="(row, i) in modelValue" :key="i" class="hrow">
      <HInput
        :model-value="row.key"
        placeholder="名称（如 X-Auth）"
        size="sm"
        :error="keyError(row) !== null"
        @update:model-value="(v: string) => setKey(i, v)"
      />
      <HInput
        :model-value="row.value"
        :placeholder="'值（支持 {{session_id}} / {{session_hash}}）'"
        size="sm"
        @update:model-value="(v: string) => setValue(i, v)"
      />
      <HIconButton icon="x" label="删除该行" size="sm" @click="removeRow(i)" />
      <p v-if="keyError(row)" class="hrow-error" role="alert">
        第 {{ i + 1 }} 行{{ keyError(row) }}
      </p>
    </div>
    <div class="hfoot">
      <HButton size="sm" variant="ghost" icon="plus" @click="addRow">添加一行</HButton>
      <span class="hfoot-hint">值中的模板变量会按会话展开，不预览实值。</span>
    </div>
  </div>
</template>

<style scoped>
.headers {
  display: flex;
  flex-direction: column;
  gap: var(--space-1);
  min-width: 0;
}
.headers-empty {
  font-size: var(--text-xs);
  color: hsl(var(--fg-subtle));
}
.hrow {
  display: grid;
  grid-template-columns: minmax(0, 1fr) minmax(0, 1.4fr) auto;
  gap: var(--space-1);
  align-items: start;
}
.hrow-error {
  grid-column: 1 / -1;
  margin: 0;
  font-size: var(--text-xs);
  color: hsl(var(--danger));
  line-height: 1.4;
}
.hfoot {
  display: flex;
  align-items: center;
  gap: var(--space-2);
  flex-wrap: wrap;
}
.hfoot-hint {
  font-size: var(--text-2xs);
  color: hsl(var(--fg-subtle));
}
</style>

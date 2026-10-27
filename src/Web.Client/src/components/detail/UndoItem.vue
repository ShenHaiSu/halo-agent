<!-- src/Web.Client/src/components/detail/UndoItem.vue -->
<!-- 撤销快照项（05 §1.5）：时间 / 文件数 / 变化徽章 / 行数文案 / 恢复按钮。 -->
<script setup lang="ts">
import { computed } from 'vue';
import type { SnapshotInfo, SnapshotFileInfo } from '@/stores/undo';
import HButton from '@/components/base/HButton.vue';
import { formatDateTime } from '@/utils/format';

const props = defineProps<{
  item: SnapshotInfo;
  expanded: boolean;
  disabled: boolean;
}>();

defineEmits<{ toggle: []; rollback: [] }>();

const fileCount = computed(() => props.item.files.length);

const summary = computed(() => {
  const names = props.item.files.slice(0, 3).map((f) => f.relPath);
  const rest = fileCount.value - names.length;
  return rest > 0 ? `${names.join('、')}等 ${fileCount.value} 个` : names.join('、');
});

function changeText(c: 'created' | 'modified' | 'deleted'): string {
  if (c === 'created') return '新增';
  if (c === 'deleted') return '删除';
  return '修改';
}

function changeClass(c: 'created' | 'modified' | 'deleted'): string {
  if (c === 'created') return 'ch-add';
  if (c === 'deleted') return 'ch-del';
  return 'ch-mod';
}

function linesText(f: SnapshotFileInfo): string {
  if (f.linesCurrent === null) return `原 ${f.linesBefore} 行`;
  if (f.linesBefore === f.linesCurrent) return `${f.linesBefore} 行（行数未变）`;
  return `${f.linesBefore} 行 → ${f.linesCurrent} 行`;
}
</script>

<template>
  <div class="undo-item" :class="{ unavailable: !item.available }">
    <button class="ui-head" type="button" :aria-expanded="expanded" @click="$emit('toggle')">
      <span class="ui-dot" aria-hidden="true" />
      <span class="ui-time">{{ formatDateTime(item.ts) }}</span>
      <span class="ui-count">{{ fileCount }} 个文件</span>
      <span class="ui-summary" :title="summary">{{ summary }}</span>
    </button>

    <div v-if="expanded" class="ui-body">
      <div v-if="item.toolName" class="ui-tool">
        由工具 <code>{{ item.toolName }}</code> 创建
      </div>
      <ul class="ui-files">
        <li v-for="f in item.files" :key="f.path" class="ui-file">
          <span class="uf-badge" :class="changeClass(f.change)">{{ changeText(f.change) }}</span>
          <span class="uf-path" :title="f.path">{{ f.relPath }}</span>
          <span class="uf-lines">{{ linesText(f) }}</span>
        </li>
      </ul>
      <div class="ui-actions">
        <span v-if="!item.available" class="ui-gone">该快照已被清理，无法恢复</span>
        <span class="spacer" />
        <HButton
          size="sm"
          variant="danger"
          :disabled="disabled || !item.available"
          @click="$emit('rollback')"
        >
          恢复到此快照
        </HButton>
      </div>
    </div>
  </div>
</template>

<style scoped>
.undo-item {
  border-bottom: 1px solid hsl(var(--border-subtle));
}
.undo-item.unavailable {
  opacity: 0.55;
}
.ui-head {
  display: flex;
  align-items: center;
  gap: var(--space-2);
  width: 100%;
  padding: var(--space-2) var(--space-3);
  background: transparent;
  border: 0;
  cursor: pointer;
  text-align: left;
  font-size: var(--text-xs);
  color: hsl(var(--fg-muted));
  min-width: 0;
}
.ui-head:hover {
  background: hsl(var(--bg-subtle));
}
.ui-dot {
  width: 6px;
  height: 6px;
  border-radius: 50%;
  background: hsl(var(--accent));
  flex: none;
}
.ui-time {
  font-family: var(--font-mono);
  color: hsl(var(--fg));
  flex: none;
}
.ui-count {
  color: hsl(var(--fg-subtle));
  flex: none;
}
.ui-summary {
  min-width: 0;
  overflow: hidden;
  text-overflow: ellipsis;
  white-space: nowrap;
}
.ui-body {
  padding: 0 var(--space-3) var(--space-2);
  border-top: 1px solid hsl(var(--border-subtle));
}
.ui-tool {
  font-size: var(--text-2xs);
  color: hsl(var(--fg-subtle));
  padding: var(--space-1) 0;
}
.ui-tool code {
  font-family: var(--font-mono);
}
.ui-files {
  list-style: none;
  margin: 0;
  padding: 0;
}
.ui-file {
  display: flex;
  align-items: center;
  gap: var(--space-2);
  padding: 2px 0;
  font-size: var(--text-xs);
  min-width: 0;
}
.uf-badge {
  flex: none;
  font-size: var(--text-2xs);
  padding: 0 4px;
  border-radius: var(--radius-xs);
}
.ch-add {
  background: hsl(var(--success-subtle));
  color: hsl(var(--success));
}
.ch-del {
  background: hsl(var(--danger-subtle));
  color: hsl(var(--danger));
}
.ch-mod {
  background: hsl(var(--bg-muted));
  color: hsl(var(--fg-muted));
}
.uf-path {
  min-width: 0;
  font-family: var(--font-mono);
  overflow: hidden;
  text-overflow: ellipsis;
  white-space: nowrap;
}
.uf-lines {
  flex: none;
  color: hsl(var(--fg-subtle));
  font-family: var(--font-mono);
}
.ui-actions {
  display: flex;
  align-items: center;
  gap: var(--space-2);
  padding-top: var(--space-2);
}
.ui-gone {
  font-size: var(--text-2xs);
  color: hsl(var(--fg-subtle));
}
.spacer {
  flex: 1;
  min-width: 0;
}
</style>

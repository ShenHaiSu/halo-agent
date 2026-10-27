<!-- src/Web.Client/src/components/detail/UndoPane.vue -->
<script setup lang="ts">
import { onMounted, ref } from 'vue';
import { useUndoStore, type SnapshotInfo } from '@/stores/undo';
import { useToast } from '@/composables/useToast';
import HButton from '@/components/base/HButton.vue';
import HEmpty from '@/components/base/HEmpty.vue';
import HSpinner from '@/components/base/HSpinner.vue';
import HDialog from '@/components/base/HDialog.vue';
import HaloIcon from '@/components/icons/HaloIcon.vue';
import UndoItem from './UndoItem.vue';

const undo = useUndoStore();
const toast = useToast();

const confirmIndex = ref<number | null>(null);
const confirmFiles = ref<string[]>([]);

function askRollback(it: SnapshotInfo): void {
  confirmIndex.value = it.index;
  confirmFiles.value = it.files.map((f) => f.relPath);
}

async function doRollback(): Promise<void> {
  if (confirmIndex.value === null) return;
  const idx = confirmIndex.value;
  confirmIndex.value = null;
  const r = await undo.rollback(idx);
  if (r.ok) toast.success('已恢复到该快照');
  else toast.error(`恢复失败：${r.message ?? '未知原因'}`);
}

onMounted(() => void undo.load());
</script>

<template>
  <div class="undo-pane">
    <div class="pane-head">
      <span class="pane-title">撤销历史</span>
      <HSpinner v-if="undo.loading" size="sm" />
      <span class="spacer" />
      <HButton size="sm" variant="ghost" :disabled="undo.loading" @click="undo.load()"
        >刷新</HButton
      >
    </div>

    <div v-if="undo.error" class="pane-error" role="alert">
      <HaloIcon name="alert" :size="14" />
      <span>读取失败：{{ undo.error }}</span>
      <HButton size="sm" variant="ghost" @click="undo.load()">重试</HButton>
    </div>

    <HEmpty
      v-else-if="undo.isEmpty"
      icon="history"
      title="还没有可撤销的改动"
      description="工具修改文件时会自动创建快照。恢复快照会把文件还原到那一刻。"
    />

    <div v-else class="undo-list">
      <UndoItem
        v-for="it in undo.shown"
        :key="it.index"
        :item="it"
        :expanded="undo.previewIndex === it.index"
        :disabled="undo.rollingBack || !it.available"
        @toggle="undo.togglePreview(it.index)"
        @rollback="askRollback(it)"
      />
      <div v-if="undo.hidden > 0" class="undo-more">
        还有 {{ undo.hidden }} 条更早的快照（仅显示最近 {{ undo.maxDisplay }} 条）
      </div>
    </div>

    <HDialog
      :open="confirmIndex !== null"
      title="恢复到这个快照？"
      width="sm"
      @update:open="confirmIndex = $event ? confirmIndex : null"
    >
      <p class="rb-p">
        以下 {{ confirmFiles.length }} 个文件将被恢复到该时刻的内容，
        <strong>当前的改动会丢失</strong>：
      </p>
      <ul class="rb-list">
        <li v-for="f in confirmFiles" :key="f">{{ f }}</li>
      </ul>
      <p v-if="undo.rollingBack" class="rb-busy" role="status">
        <HSpinner size="sm" />
        <span>正在恢复 {{ confirmFiles.length }} 个文件…</span>
      </p>
      <template #footer>
        <HButton
          variant="secondary"
          size="sm"
          :disabled="undo.rollingBack"
          @click="confirmIndex = null"
          >取消</HButton
        >
        <HButton variant="danger" size="sm" :loading="undo.rollingBack" @click="doRollback"
          >恢复</HButton
        >
      </template>
    </HDialog>
  </div>
</template>

<style scoped>
.undo-pane {
  display: flex;
  flex-direction: column;
  height: 100%;
  min-height: 0;
}
.pane-head {
  display: flex;
  align-items: center;
  gap: var(--space-2);
  padding: var(--space-2) var(--space-3);
  border-bottom: 1px solid hsl(var(--border));
  flex: none;
}
.pane-title {
  font-size: var(--text-sm);
  font-weight: 600;
}
.spacer {
  flex: 1;
  min-width: 0;
}
.pane-error {
  display: flex;
  align-items: center;
  gap: var(--space-2);
  padding: var(--space-2) var(--space-3);
  background: hsl(var(--danger-subtle));
  color: hsl(var(--danger));
  font-size: var(--text-xs);
}
.undo-list {
  flex: 1;
  min-height: 0;
  overflow-y: auto;
}
.undo-more {
  padding: var(--space-2) var(--space-3);
  font-size: var(--text-xs);
  color: hsl(var(--fg-subtle));
  text-align: center;
}
.rb-p {
  margin: 0 0 var(--space-2);
  font-size: var(--text-sm);
  line-height: 1.6;
}
.rb-list {
  margin: 0;
  padding-left: 1.2em;
  max-height: 200px;
  overflow: auto;
  font-family: var(--font-mono);
  font-size: var(--text-xs);
}
.rb-busy {
  display: flex;
  align-items: center;
  gap: var(--space-1);
  margin: var(--space-2) 0 0;
  font-size: var(--text-xs);
  color: hsl(var(--fg-muted));
}
</style>

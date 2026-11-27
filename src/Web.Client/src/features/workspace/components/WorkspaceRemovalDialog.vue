<template>
  <n-modal
    :show="open"
    preset="card"
    :title="`移除工作区「${displayName}」`"
    style="width: 560px; max-width: 94vw"
    :mask-closable="!busy"
    :close-on-esc="!busy"
    data-testid="workspace-removal-dialog"
    @update:show="onShowChange"
  >
    <div class="space-y-3">
      <n-alert v-if="!canRemove" type="warning" :bordered="false">
        当前宿主不支持工作区双模式移除，请升级宿主后再试。旧入口只会解绑，无法彻底删除。
      </n-alert>

      <n-radio-group :value="selectedMode" :disabled="busy || !canRemove" data-testid="workspace-removal-mode" @update:value="onModeChange">
        <div class="space-y-2">
          <n-radio value="unbind" data-testid="workspace-removal-mode-unbind">
            <span class="text-xs font-semibold">仅从列表移除（解绑）</span>
          </n-radio>
          <p class="pl-6 text-[11px] text-secondary leading-snug">
            保留会话数据（history.db、备份与所有会话）。若项目 .haloagent 中没有 skill/memory 且无其它内容，会清理这个空目录。
          </p>
          <n-radio value="purge" data-testid="workspace-removal-mode-purge">
            <span class="text-xs font-semibold text-error">彻底删除</span>
          </n-radio>
          <p class="pl-6 text-[11px] text-secondary leading-snug">
            删除该工作区的全部会话数据、history.db 与备份，以及项目 .haloagent（含 skill/memory）。不会删除项目其它文件。此操作不可恢复。
          </p>
        </div>
      </n-radio-group>

      <div v-if="selectedMode === 'purge'" class="space-y-2">
        <n-alert type="error" :bordered="false">此操作不可恢复，请确认已备份需要保留的数据。</n-alert>
        <label class="block text-[11px] text-secondary" for="workspace-removal-confirm">
          输入工作区名称「{{ displayName }}」以确认彻底删除
        </label>
        <n-input
          id="workspace-removal-confirm"
          :value="confirmText"
          :disabled="busy || !canRemove"
          placeholder="请输入工作区显示名称"
          data-testid="workspace-removal-confirm-input"
          @update:value="onConfirmTextChange"
        />
      </div>
    </div>

    <template #footer>
      <div class="flex justify-end gap-2">
        <n-button :disabled="busy" @click="emit('cancel')">取消</n-button>
        <n-button
          type="error"
          :disabled="!canConfirm"
          :loading="busy"
          data-testid="workspace-removal-confirm-btn"
          @click="emit('confirm', selectedMode, confirmText)"
        >
          {{ selectedMode === 'purge' ? '彻底删除' : '仅从列表移除' }}
        </n-button>
      </div>
    </template>
  </n-modal>
</template>
<script setup lang="ts">
import { computed, ref, watch } from 'vue';
import type { WorkspaceInfo, WorkspaceRemovalMode } from '@shared/bridge/hostBridge';
import { isRemovalConfirmed, removalConfirmName } from '../utils/workspaceRemoval';

const props = defineProps<{
  open: boolean;
  workspace: WorkspaceInfo | null;
  canRemove: boolean;
  busy: boolean;
}>();
const emit = defineEmits<{
  cancel: [];
  confirm: [mode: WorkspaceRemovalMode, confirmText: string];
  'update:show': [value: boolean];
}>();

const selectedMode = ref<WorkspaceRemovalMode>('unbind');
const confirmText = ref('');
const displayName = computed(() => removalConfirmName(props.workspace));
const canConfirm = computed(
  () => props.canRemove && !props.busy && isRemovalConfirmed(selectedMode.value, props.workspace, confirmText.value)
);

watch(
  () => props.open,
  (open) => {
    if (!open) return;
    selectedMode.value = 'unbind';
    confirmText.value = '';
  }
);

function onModeChange(value: WorkspaceRemovalMode): void {
  if (props.busy) return;
  selectedMode.value = value;
  confirmText.value = '';
}
function onConfirmTextChange(value: string): void {
  if (!props.busy) confirmText.value = value;
}
function onShowChange(value: boolean): void {
  if (!value) emit('cancel');
  else emit('update:show', true);
}
</script>

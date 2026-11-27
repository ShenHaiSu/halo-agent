import { computed, ref } from 'vue';
import { useMessage } from 'naive-ui';
import { supportsHostBridgeMethod, type WorkspaceInfo, type WorkspaceRemovalMode } from '@shared/bridge/hostBridge';
import { useWorkspaceStore } from '@/stores/workspaceStore';
import { isRemovalConfirmed, removalFeedback } from '../utils/workspaceRemoval';

/**
 * 工作区移除交互状态机：能力门 → 模式选择 → Purge 强确认 → 结果反馈。
 * 目标 workspaceId 在打开时捕获，异步返回时不使用当前 active 猜测目标。
 */
export function useWorkspaceRemoval() {
  const message = useMessage();
  const workspaceStore = useWorkspaceStore();

  const canRemoveWorkspace = ref(supportsHostBridgeMethod('removeWorkspaceV2'));
  const dialogOpen = ref(false);
  const targetWorkspaceId = ref<string | null>(null);
  const mode = ref<WorkspaceRemovalMode>('unbind');
  const confirmText = ref('');
  const busy = ref(false);

  const targetWorkspace = computed<WorkspaceInfo | null>(
    () => workspaceStore.workspaces.find((ws) => ws.id === targetWorkspaceId.value) ?? null
  );
  const confirmValid = computed(
    () => canRemoveWorkspace.value && !busy.value && isRemovalConfirmed(mode.value, targetWorkspace.value, confirmText.value)
  );

  function resetDraft(): void {
    mode.value = 'unbind';
    confirmText.value = '';
  }

  function requestRemoval(workspaceId: string): void {
    if (busy.value) return;
    if (!canRemoveWorkspace.value) {
      message.warning('当前宿主不支持工作区双模式移除，请升级后重试');
      return;
    }
    if (workspaceStore.workspaces.length <= 1) {
      message.warning('至少需要保留一个工作区');
      return;
    }
    if (!workspaceStore.workspaces.some((ws) => ws.id === workspaceId)) return;
    targetWorkspaceId.value = workspaceId;
    resetDraft();
    dialogOpen.value = true;
  }

  function cancelRemoval(): void {
    if (busy.value) return;
    dialogOpen.value = false;
    targetWorkspaceId.value = null;
    resetDraft();
  }

  function setMode(next: WorkspaceRemovalMode): void {
    if (busy.value || next === mode.value) return;
    mode.value = next;
    confirmText.value = '';
  }

  function setConfirmText(value: string): void {
    if (busy.value) return;
    confirmText.value = value;
  }

  /** 由弹窗回传当前模式与输入；busy 同步置位，双击只会发出一次请求。 */
  async function confirmRemoval(nextMode?: WorkspaceRemovalMode, nextText?: string): Promise<void> {
    if (busy.value || !dialogOpen.value || !targetWorkspaceId.value) return;
    if (typeof nextMode === 'string' && nextMode !== mode.value) setMode(nextMode);
    if (typeof nextText === 'string') setConfirmText(nextText);
    if (!confirmValid.value) return;

    const targetId = targetWorkspaceId.value;
    const submittedMode = mode.value;
    busy.value = true;
    try {
      const result = await workspaceStore.removeWorkspaceV2(targetId, submittedMode);
      const feedback = removalFeedback(result);
      message[feedback.level](feedback.text);
      dialogOpen.value = false;
      targetWorkspaceId.value = null;
      resetDraft();
    } finally {
      busy.value = false;
    }
  }

  return {
    canRemoveWorkspace,
    dialogOpen,
    targetWorkspaceId,
    targetWorkspace,
    mode,
    confirmText,
    confirmValid,
    busy,
    requestRemoval,
    cancelRemoval,
    setMode,
    setConfirmText,
    confirmRemoval,
  };
}

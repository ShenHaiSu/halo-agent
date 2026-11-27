import { computed, reactive, ref } from 'vue';
import { useDialog, useMessage } from 'naive-ui';
import { getHistoryCapabilitiesState } from '@shared/bridge/hostBridge';
import { historyErrorMessage } from '@shared/bridge/protocol/errors';
import { useWorkspaceStore } from '@/stores/workspaceStore';
import { useAgentTurn } from '@features/agent/stores/useAgentTurn';
import { useAgentStream } from '@features/agent/stores/useAgentStream';
import { copyTextToClipboard, type HistoryTarget } from '@features/session/utils/historyAnchor';

export interface TurnMenuTarget extends HistoryTarget {
  /** 整轮文本（复制整轮问答用；单条仍走 text）。 */
  turnText?: string;
  /** 复制此消息按钮文案（按 kind 区分：复制此提问 / 复制此回复）。 */
  copyLabel?: string;
}

export function useTimelineMenu() {
  const workspaceStore = useWorkspaceStore();
  const turn = useAgentTurn();
  const stream = useAgentStream();
  const dialog = useDialog();
  const message = useMessage();
  const menuState = reactive<{ visible: boolean; x: number; y: number; target: TurnMenuTarget | null }>({
    visible: false,
    x: 0,
    y: 0,
    target: null,
  });
  const historyActionInFlight = ref(false);
  const menuBusy = computed(() => {
    const sid = workspaceStore.activeSessionId || '';
    return historyActionInFlight.value || stream.isGenerating || stream.streamingActive || (sid ? turn.isCompacting(sid) : false);
  });
  async function runHistoryAction(action: () => Promise<void>): Promise<void> {
    if (menuBusy.value) return;
    historyActionInFlight.value = true;
    try {
      await action();
    } finally {
      historyActionInFlight.value = false;
    }
  }
  const menuCanCopy = computed(() => String(menuState.target?.text || '').length > 0);
  const menuCanCopyTurn = computed(() => String(menuState.target?.turnText || '').length > 0);
  const menuCanDelete = computed(
    () => String(menuState.target?.id || '').length > 0 && getHistoryCapabilitiesState().capabilities.turnDelete
  );
  const menuCopyLabel = computed(() => String(menuState.target?.copyLabel || '复制此消息').trim() || '复制此消息');
  function openMenu(e: MouseEvent, target: TurnMenuTarget): void {
    openTurnMenu(e, target);
  }
  /** 轮级菜单入口：复制此消息=单条，重放/分支/删除=真实消息锚点；另有复制整轮问答。 */
  function openTurnMenu(e: MouseEvent, target: TurnMenuTarget): void {
    if (!target.id && !target.text) return;
    menuState.x = Math.max(8, Math.min(e.clientX, window.innerWidth - 200));
    menuState.y = Math.max(8, Math.min(e.clientY, window.innerHeight - 192));
    menuState.target = target;
    menuState.visible = true;
  }
  function closeMenu(): void {
    menuState.visible = false;
  }
  async function onMenuCopy(): Promise<void> {
    const target = menuState.target;
    closeMenu();
    if (!target?.text) return;
    const ok = await copyTextToClipboard(target.text);
    message[ok ? 'success' : 'error'](ok ? '已复制' : '复制失败');
    if (ok === false) console.warn('[history] copy failed');
  }
  async function onMenuCopyTurn(): Promise<void> {
    const target = menuState.target;
    closeMenu();
    if (!target?.turnText) return;
    const ok = await copyTextToClipboard(target.turnText);
    message[ok ? 'success' : 'error'](ok ? '已复制整轮' : '复制失败');
    if (ok === false) console.warn('[history] copy turn failed');
  }
  async function onMenuReplay(): Promise<void> {
    const target = menuState.target;
    closeMenu();
    if (!target || target.canReplay === false || menuBusy.value) return;
    await runHistoryAction(async () => {
      const result = target.kind === 'user' ? await turn.replayUserMessage(target.id) : await turn.replayAssistantTurn(target.id);
      if (!result.ok && result.message) message.error(result.message);
      else if (result.warnings?.length) message.warning(result.warnings.map((w) => w.message).join('；'));
    });
  }
  async function onMenuBranch(): Promise<void> {
    const target = menuState.target;
    closeMenu();
    if (!target || target.canBranch === false || menuBusy.value) return;
    await runHistoryAction(async () => {
      const result = await workspaceStore.branchFromMessage(target.id, target.kind, target.draftText);
      if (!result.ok) {
        message.error(result.message || '分支失败');
        return;
      }
      if (result.newSessionId) {
        message.error(`分支清理失败，残留会话 ${result.newSessionId}（阶段 ${result.failedStage || '未知'}），请手动处理`);
        return;
      }
      if (result.switched === false) message.warning('分支已创建，但当前会话已切换，请手动打开');
      else message.success('分支已创建');
    });
  }
  function onMenuDelete(): void {
    const target = menuState.target;
    closeMenu();
    if (!target?.id || !menuCanDelete.value || menuBusy.value) return;
    const anchorId = target.id;
    dialog.warning({
      title: '删除本轮',
      content: '确定删除这一轮吗？将删除该轮提问、回复及工具记录，其他轮次不受影响。删除前会自动备份。',
      positiveText: '删除本轮',
      negativeText: '取消',
      onPositiveClick: async () => {
        // 弹窗停留期间可能开始生成/压缩，确认瞬间再次复核忙态与能力。
        if (menuBusy.value || !menuCanDelete.value) return;
        await runHistoryAction(async () => {
          const result = await workspaceStore.deleteTurn(anchorId);
          if (!result.ok) message.error(result.message || historyErrorMessage(result.code, '整轮删除失败'));
          else if (result.metadataWarning) message.warning('本轮已删除，但元数据刷新失败，请刷新确认');
          else message.success('本轮已删除');
        });
      },
    });
  }
  function bindMenuGlobalClose(): void {
    window.addEventListener('click', closeMenu);
    window.addEventListener('resize', closeMenu);
    window.addEventListener('keydown', (e) => {
      if (e.key === 'Escape') closeMenu();
    });
  }
  return {
    menuState,
    menuBusy,
    historyActionInFlight,
    menuCanCopy,
    menuCanCopyTurn,
    menuCanDelete,
    menuCopyLabel,
    openMenu,
    openTurnMenu,
    closeMenu,
    onMenuCopy,
    onMenuCopyTurn,
    onMenuReplay,
    onMenuBranch,
    onMenuDelete,
    bindMenuGlobalClose,
  };
}
export type TimelineMenu = ReturnType<typeof useTimelineMenu>;

import { computed, reactive } from 'vue';
import { useDialog } from 'naive-ui';
import { useWorkspaceStore } from '@/stores/workspaceStore';
import { useAgentTurn } from '@features/agent/stores/useAgentTurn';
import { useAgentStream } from '@features/agent/stores/useAgentStream';
import { copyTextToClipboard, type HistoryTarget } from '@features/session/utils/historyAnchor';

export function useTimelineMenu() {
  const workspaceStore = useWorkspaceStore();
  const turn = useAgentTurn();
  const stream = useAgentStream();
  const dialog = useDialog();
  const menuState = reactive<{ visible: boolean; x: number; y: number; target: HistoryTarget | null }>({
    visible: false,
    x: 0,
    y: 0,
    target: null,
  });
  const menuBusy = computed(() => {
    const sid = workspaceStore.activeSessionId || '';
    return stream.isGenerating || stream.streamingActive || (sid ? turn.isCompacting(sid) : false);
  });
  const menuCanCopy = computed(() => String(menuState.target?.text || '').length > 0);
  function openMenu(e: MouseEvent, target: HistoryTarget): void {
    if (!target.id) return;
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
    try {
      (window as unknown as { $message?: { success: (s: string) => void; error: (s: string) => void } }).$message?.[
        ok ? 'success' : 'error'
      ]?.(ok ? '已复制' : '复制失败');
    } catch {
      /* ignore */
    }
    if (ok === false) console.warn('[history] copy failed');
  }
  async function onMenuReplay(): Promise<void> {
    const target = menuState.target;
    closeMenu();
    if (!target || menuBusy.value) return;
    if (target.kind === 'user') await turn.replayUserMessage(target.id);
    else await turn.replayAssistantTurn(target.id);
  }
  async function onMenuBranch(): Promise<void> {
    const target = menuState.target;
    closeMenu();
    if (!target || menuBusy.value) return;
    await workspaceStore.branchFromMessage(target.id, target.kind);
  }
  function onMenuDelete(): void {
    const target = menuState.target;
    closeMenu();
    if (!target?.id || menuBusy.value) return;
    const label = target.kind === 'user' ? '这条用户消息' : '本轮 assistant 回复';
    dialog.warning({
      title: '删除消息',
      content: `确定删除${label}吗？删除前会自动备份；若因此缺失工具回包，会自动补中断占位，保证历史仍可继续对话。`,
      positiveText: '删除',
      negativeText: '取消',
      onPositiveClick: async () => {
        const res = await workspaceStore.deleteMessage(target.id);
        try {
          (window as unknown as { $message?: { success: (s: string) => void; error: (s: string) => void } }).$message?.[
            res.ok ? 'success' : 'error'
          ]?.(res.ok ? '已删除' : res.message || '删除失败');
        } catch {
          /* ignore */
        }
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
    menuCanCopy,
    openMenu,
    closeMenu,
    onMenuCopy,
    onMenuReplay,
    onMenuBranch,
    onMenuDelete,
    bindMenuGlobalClose,
  };
}
export type TimelineMenu = ReturnType<typeof useTimelineMenu>;

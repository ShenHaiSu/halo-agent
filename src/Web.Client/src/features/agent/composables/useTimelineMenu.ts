import { computed, reactive } from 'vue';
import { useDialog } from 'naive-ui';
import { useWorkspaceStore } from '@/stores/workspaceStore';
import { useAgentTurn } from '@features/agent/stores/useAgentTurn';
import { useAgentStream } from '@features/agent/stores/useAgentStream';
import { copyTextToClipboard, type HistoryTarget } from '@features/session/utils/historyAnchor';

export interface TurnMenuTarget extends HistoryTarget {
  /** need02-P4（P4b#5）：整轮消息 id（删除时逐条删，二次确认只一次）。 */
  turnIds?: string[];
  /** 整轮标签（删除确认文案用，如“整轮（5条）”）。 */
  turnLabel?: string;
}

export function useTimelineMenu() {
  const workspaceStore = useWorkspaceStore();
  const turn = useAgentTurn();
  const stream = useAgentStream();
  const dialog = useDialog();
  const menuState = reactive<{ visible: boolean; x: number; y: number; target: TurnMenuTarget | null }>({
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
    openTurnMenu(e, target);
  }
  /** need02-P4（P4b#5）：轮级菜单入口（复制=整轮全文，重放/分支=轮锚点，删除=整轮逐条）。 */
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
    // need02-P4（P4b#5）：整轮删除（二次确认一次，确认后逐条删；后端复用 deleteMessage 单条语义）。
    const turnIds = Array.isArray(target.turnIds) ? target.turnIds.filter((id) => !!String(id || '').trim()) : [];
    const label = target.turnLabel || (target.kind === 'user' ? '这条用户消息' : '本轮 assistant 回复');
    dialog.warning({
      title: '删除消息',
      content: `确定删除${label}吗？删除前会自动备份；若因此缺失工具回包，会自动补中断占位，保证历史仍可继续对话。`,
      positiveText: '删除',
      negativeText: '取消',
      onPositiveClick: async () => {
        if (turnIds.length > 1) {
          let okCount = 0;
          let lastMsg = '';
          for (const id of turnIds) {
            try {
              const res = await workspaceStore.deleteMessage(id);
              if (res.ok) okCount++;
              else lastMsg = res.message || '删除失败';
            } catch (err) {
              lastMsg = err instanceof Error ? err.message : String(err);
            }
          }
          try {
            (window as unknown as { $message?: { success: (s: string) => void; error: (s: string) => void } }).$message?.[
              okCount === turnIds.length ? 'success' : 'error'
            ]?.(okCount === turnIds.length ? `已删除整轮（${okCount}条）` : `部分删除失败（${okCount}/${turnIds.length}）：${lastMsg}`);
          } catch {
            /* ignore */
          }
          return;
        }
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
    openTurnMenu,
    closeMenu,
    onMenuCopy,
    onMenuReplay,
    onMenuBranch,
    onMenuDelete,
    bindMenuGlobalClose,
  };
}
export type TimelineMenu = ReturnType<typeof useTimelineMenu>;

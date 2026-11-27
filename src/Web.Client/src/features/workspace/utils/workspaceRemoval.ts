import type { WorkspaceInfo, WorkspaceRemovalCommandResult, WorkspaceRemovalMode } from '@shared/bridge/hostBridge';

/** 强确认使用界面显示名：别名优先，空则回退文件夹名。 */
export function removalConfirmName(workspace: WorkspaceInfo | null | undefined): string {
  if (!workspace) return '';
  const alias = (workspace.alias ?? '').trim();
  return alias || (workspace.name ?? '').trim();
}

/** Purge 必须完全匹配且非空；解绑无需强确认。 */
export function isRemovalConfirmed(
  mode: WorkspaceRemovalMode,
  workspace: WorkspaceInfo | null | undefined,
  input: string | null | undefined
): boolean {
  if (mode !== 'purge') return true;
  const expected = removalConfirmName(workspace);
  return expected.length > 0 && (input ?? '').trim() === expected;
}

export interface RemovalFeedback {
  level: 'success' | 'warning' | 'error' | 'info';
  text: string;
}

/** 结果文案与后端分阶段状态一一对应，不使用“不会删除磁盘文件”这类模糊表述。 */
export function removalFeedback(result: WorkspaceRemovalCommandResult): RemovalFeedback {
  if (result.outcome === 'failed') {
    return { level: 'error', text: '移除失败，工作区仍在列表中' };
  }
  if (result.outcome === 'notFound') {
    return { level: 'info', text: '工作区不存在，列表未变更' };
  }
  if (result.outcome === 'completedWithWarnings') {
    const count = result.failedPaths.length;
    if (result.mode === 'unbind' && result.haloAgent === 'retained' && count === 0) {
      return { level: 'warning', text: '已移出列表；会话数据与 .haloagent 已保留' };
    }
    const detail = count > 0 ? `（${count} 个路径需人工检查）` : '';
    return {
      level: 'warning',
      text: `已移出列表，但部分物理数据清理失败或被保守保留${detail}`,
    };
  }
  if (result.mode === 'purge') {
    return { level: 'success', text: '工作区已彻底删除' };
  }
  if (result.haloAgent === 'deleted') {
    return { level: 'success', text: '已移出列表；会话数据已保留，空 .haloagent 已清理' };
  }
  return { level: 'info', text: '已移出列表；会话数据与 .haloagent 已保留' };
}

import { describe, expect, it } from 'vitest';
import type { WorkspaceInfo, WorkspaceRemovalCommandResult } from '@shared/bridge/hostBridge';
import { isRemovalConfirmed, removalConfirmName, removalFeedback } from './workspaceRemoval';

function result(overrides: Partial<WorkspaceRemovalCommandResult> = {}): WorkspaceRemovalCommandResult {
  return {
    outcome: 'completed',
    mode: 'unbind',
    workspaceUnbound: true,
    sessionData: 'notRequested',
    haloAgent: 'deleted',
    failedPaths: [],
    warnings: [],
    ...overrides,
  };
}

describe('工作区移除文案与确认规则', () => {
  it('别名优先，空别名回退文件夹名', () => {
    expect(removalConfirmName({ name: '文件夹名', alias: '别名优先' } as WorkspaceInfo)).toBe('别名优先');
    expect(removalConfirmName({ name: '文件夹名', alias: '  ' } as WorkspaceInfo)).toBe('文件夹名');
    expect(removalConfirmName(null)).toBe('');
  });

  it('解绑无需强确认，彻底删除要求完全匹配且非空', () => {
    const ws = { name: '文件夹名', alias: '别名优先' } as WorkspaceInfo;
    expect(isRemovalConfirmed('unbind', ws, '')).toBe(true);
    expect(isRemovalConfirmed('purge', ws, '')).toBe(false);
    expect(isRemovalConfirmed('purge', ws, '别名')).toBe(false);
    expect(isRemovalConfirmed('purge', ws, '别名优先')).toBe(true);
  });

  it('每种后端结果都有对应反馈，且不使用误导性“不删磁盘文件”文案', () => {
    expect(removalFeedback(result()).text).toContain('空 .haloagent 已清理');
    expect(removalFeedback(result({ haloAgent: 'retained' })).text).toContain('已保留');
    expect(removalFeedback(result({ mode: 'purge' })).text).toBe('工作区已彻底删除');
    expect(removalFeedback(result({ outcome: 'completedWithWarnings', failedPaths: ['path-a'] }))).toMatchObject({
      level: 'warning',
    });
    expect(removalFeedback(result({ outcome: 'completedWithWarnings', haloAgent: 'retained' })).text).toBe(
      '已移出列表；会话数据与 .haloagent 已保留'
    );
    expect(removalFeedback(result({ outcome: 'failed', workspaceUnbound: false }))).toMatchObject({
      level: 'error',
    });
    expect(removalFeedback(result({ outcome: 'notFound' }))).toMatchObject({ level: 'info' });
    expect(JSON.stringify(removalFeedback(result()))).not.toContain('不会删除磁盘文件');
  });
});

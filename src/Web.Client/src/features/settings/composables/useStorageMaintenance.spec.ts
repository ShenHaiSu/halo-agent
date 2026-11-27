import { describe, expect, it, vi, beforeEach } from 'vitest';
import { describeMaintenanceError, selectDatabaseDetails, summarizeDatabaseDetails } from './useStorageMaintenance';
import type { StorageBreakdown } from '@shared/bridge/hostBridge';

vi.mock('@shared/bridge/hostBridge', () => ({
  supportsHostBridgeMethod: vi.fn(),
}));

vi.mock('@/stores/systemStore', () => ({
  useSystemStore: () => ({}),
}));

const breakdown: StorageBreakdown = {
  rootPath: 'D:\\HaloAgentData',
  totalBytes: 4096,
  items: [
    {
      name: 'sessions',
      sizeBytes: 2048,
      fileCount: 8,
      databaseDetails: [
        {
          workspaceId: 'ws_a',
          databasePath: 'D:\\HaloAgentData\\sessions\\ws_a\\history.db',
          physicalBytes: 1536,
          pageSizeBytes: 4096,
          pageCount: 8,
          freePageCount: 5,
          usedPageBytes: 12288,
          reclaimablePageBytes: 20480,
          backupBytes: 512,
          walBytes: 64,
          shmBytes: 32,
          tempBytes: 0,
          measured: true,
          warning: null,
          measuredAtUtc: '2026-11-27T00:00:00Z',
        },
        {
          workspaceId: 'ws_b',
          databasePath: 'D:\\HaloAgentData\\sessions\\ws_b\\history.db',
          physicalBytes: 0,
          pageSizeBytes: 0,
          pageCount: 0,
          freePageCount: 0,
          usedPageBytes: 0,
          reclaimablePageBytes: 0,
          backupBytes: 128,
          walBytes: 0,
          shmBytes: 0,
          tempBytes: 0,
          measured: false,
          warning: 'READ_FAILED',
          measuredAtUtc: '2026-11-27T00:00:00Z',
        },
      ],
    },
    { name: 'logs', sizeBytes: 2048, fileCount: 2 },
  ],
};

describe('历史库双口径选择与汇总', () => {
  beforeEach(() => {
    vi.clearAllMocks();
  });

  it('仅收敛 sessions 行明细，非 sessions 行不污染', () => {
    const details = selectDatabaseDetails(breakdown);
    expect(details.map((d) => d.workspaceId)).toEqual(['ws_a', 'ws_b']);
  });

  it('空 breakdown 返回空数组，不抛异常', () => {
    expect(selectDatabaseDetails(null)).toEqual([]);
    expect(summarizeDatabaseDetails([])).toEqual({
      totalPhysicalBytes: 0,
      totalUsedPageBytes: 0,
      totalReclaimableBytes: 0,
      totalBackupBytes: 0,
      totalWalBytes: 0,
      measuredCount: 0,
      unmeasuredCount: 0,
    });
  });

  it('未测量行不计入可回收估算，但备份仍计入', () => {
    const summary = summarizeDatabaseDetails(selectDatabaseDetails(breakdown));
    expect(summary.measuredCount).toBe(1);
    expect(summary.unmeasuredCount).toBe(1);
    expect(summary.totalReclaimableBytes).toBe(20480);
    expect(summary.totalBackupBytes).toBe(640);
    expect(summary.totalWalBytes).toBe(96);
  });

  it('错误码映射为中文提示，未知码回退兜底', () => {
    expect(describeMaintenanceError('STORAGE_MAINTENANCE_BUSY', '兜底')).toBe('存储维护正在进行，请稍后重试');
    expect(describeMaintenanceError('CHECKPOINT_BUSY', '兜底')).toBe('存储正忙，请稍后重试');
    expect(describeMaintenanceError('UNKNOWN_CODE', '兜底')).toBe('兜底');
  });
});

import { computed, ref } from 'vue';
import type {
  StorageBreakdown,
  WorkspaceBackupCleanupResult,
  WorkspaceCompactionResult,
  WorkspaceDatabaseStorageDetail,
} from '@shared/bridge/hostBridge';
import { supportsHostBridgeMethod } from '@shared/bridge/hostBridge';
import { useSystemStore } from '@/stores/systemStore';

export interface StorageMaintenanceSummary {
  totalPhysicalBytes: number;
  totalUsedPageBytes: number;
  totalReclaimableBytes: number;
  totalBackupBytes: number;
  totalWalBytes: number;
  measuredCount: number;
  unmeasuredCount: number;
}

export interface MaintenanceCallResult<T> {
  ok: boolean;
  code?: string;
  message?: string;
  data?: T | null;
}

function toNumber(value: unknown): number {
  const n = typeof value === 'number' ? value : Number(value);
  return Number.isFinite(n) && n > 0 ? n : 0;
}

/** sessions 行数据库明细：仅收敛可渲染行，统计失败行不计入可回收估算。 */
export function selectDatabaseDetails(breakdown: StorageBreakdown | null): WorkspaceDatabaseStorageDetail[] {
  const sessions = (breakdown?.items ?? []).find((item) => item.name === 'sessions');
  const details = sessions?.databaseDetails ?? [];
  return details
    .filter((d) => !!d && typeof d.workspaceId === 'string' && d.workspaceId.trim().length > 0)
    .map((d) => ({
      workspaceId: d.workspaceId,
      databasePath: d.databasePath ?? '',
      physicalBytes: toNumber(d.physicalBytes),
      pageSizeBytes: toNumber(d.pageSizeBytes),
      pageCount: toNumber(d.pageCount),
      freePageCount: toNumber(d.freePageCount),
      usedPageBytes: toNumber(d.usedPageBytes),
      reclaimablePageBytes: toNumber(d.reclaimablePageBytes),
      backupBytes: toNumber(d.backupBytes),
      walBytes: toNumber(d.walBytes),
      shmBytes: toNumber(d.shmBytes),
      tempBytes: toNumber(d.tempBytes),
      measured: d.measured === true,
      warning: d.warning ?? null,
      measuredAtUtc: d.measuredAtUtc ?? '',
    }));
}

export function summarizeDatabaseDetails(details: WorkspaceDatabaseStorageDetail[]): StorageMaintenanceSummary {
  let totalUsedPageBytes = 0;
  let totalReclaimableBytes = 0;
  let totalBackupBytes = 0;
  let totalWalBytes = 0;
  let measuredCount = 0;
  let unmeasuredCount = 0;
  for (const d of details) {
    if (!d.measured) {
      unmeasuredCount += 1;
      totalBackupBytes += toNumber(d.backupBytes);
      totalWalBytes += toNumber(d.walBytes) + toNumber(d.shmBytes);
      continue;
    }
    measuredCount += 1;
    totalUsedPageBytes += toNumber(d.usedPageBytes);
    totalReclaimableBytes += toNumber(d.reclaimablePageBytes);
    totalBackupBytes += toNumber(d.backupBytes);
    totalWalBytes += toNumber(d.walBytes) + toNumber(d.shmBytes);
  }
  return {
    totalPhysicalBytes: totalUsedPageBytes + totalReclaimableBytes,
    totalUsedPageBytes,
    totalReclaimableBytes,
    totalBackupBytes,
    totalWalBytes,
    measuredCount,
    unmeasuredCount,
  };
}

export function describeMaintenanceError(code: string | undefined, fallback: string): string {
  switch (code) {
    case 'STORAGE_UNSUPPORTED':
      return '当前宿主不支持该维护能力，请升级后重试';
    case 'INVALID_WORKSPACE_ID':
      return '工作区 ID 不合法';
    case 'DATABASE_NOT_FOUND':
      return '历史库不存在，无需压实';
    case 'WORKSPACE_BUSY':
      return '工作区正在执行其他操作，请稍后重试';
    case 'SESSION_BUSY':
      return '会话正在生成或执行历史操作，请稍后重试';
    case 'HISTORY_OPERATION_IN_PROGRESS':
      return '会话正在执行其他历史操作，请稍后重试';
    case 'STORAGE_MAINTENANCE_BUSY':
      return '存储维护正在进行，请稍后重试';
    case 'CHECKPOINT_BUSY':
      return '存储正忙，请稍后重试';
    case 'DATABASE_BUSY':
      return '数据库正忙，请稍后重试';
    case 'INSUFFICIENT_DISK_SPACE':
      return '磁盘空间不足，无法执行压实';
    case 'BACKUP_DELETE_FAILED':
      return '备份删除失败，文件可能被占用';
    case 'DELETE_CANCELLED':
      return '操作已取消';
    default:
      return fallback;
  }
}

/** 存储维护编排：能力探测、忙态、结果透出与统计刷新，面板只做展示与确认。 */
export function useStorageMaintenance() {
  const systemStore = useSystemStore();
  const compacting = ref(false);
  const cleaningBackup = ref(false);

  const canCompact = computed(() => {
    try {
      return supportsHostBridgeMethod('compactWorkspaceHistory');
    } catch {
      return false;
    }
  });
  const canDeleteBackup = computed(() => {
    try {
      return supportsHostBridgeMethod('deleteWorkspaceBackup');
    } catch {
      return false;
    }
  });

  async function compactWorkspace(workspaceId: string): Promise<MaintenanceCallResult<WorkspaceCompactionResult>> {
    const id = (workspaceId ?? '').trim();
    if (!id) {
      return { ok: false, code: 'INVALID_WORKSPACE_ID', message: '工作区 ID 不能为空' };
    }
    if (!canCompact.value) {
      return { ok: false, code: 'STORAGE_UNSUPPORTED', message: describeMaintenanceError('STORAGE_UNSUPPORTED', '压实通道不可用') };
    }
    if (compacting.value) {
      return { ok: false, code: 'STORAGE_MAINTENANCE_BUSY', message: describeMaintenanceError('STORAGE_MAINTENANCE_BUSY', '压实进行中') };
    }
    compacting.value = true;
    try {
      const res = await systemStore.compactWorkspaceHistory(id);
      if (!res.ok || !res.data?.success) {
        const code = res.code || res.data?.errorCode || 'COMPACTION_FAILED';
        return { ok: false, code, message: res.message || describeMaintenanceError(code, '历史库压实失败') };
      }
      return { ok: true, message: res.message || '历史库压实完成', data: res.data };
    } finally {
      compacting.value = false;
    }
  }

  async function deleteWorkspaceBackup(workspaceId: string): Promise<MaintenanceCallResult<WorkspaceBackupCleanupResult>> {
    const id = (workspaceId ?? '').trim();
    if (!id) {
      return { ok: false, code: 'INVALID_WORKSPACE_ID', message: '工作区 ID 不能为空' };
    }
    if (!canDeleteBackup.value) {
      return { ok: false, code: 'STORAGE_UNSUPPORTED', message: describeMaintenanceError('STORAGE_UNSUPPORTED', '备份清理通道不可用') };
    }
    if (cleaningBackup.value) {
      return {
        ok: false,
        code: 'STORAGE_MAINTENANCE_BUSY',
        message: describeMaintenanceError('STORAGE_MAINTENANCE_BUSY', '备份清理进行中'),
      };
    }
    cleaningBackup.value = true;
    try {
      const res = await systemStore.deleteWorkspaceBackup(id);
      if (!res.ok || !res.data?.success) {
        const code = res.code || res.data?.errorCode || 'BACKUP_DELETE_FAILED';
        return { ok: false, code, message: res.message || describeMaintenanceError(code, '备份清理失败') };
      }
      return { ok: true, message: res.message || '备份清理完成', data: res.data };
    } finally {
      cleaningBackup.value = false;
    }
  }

  return {
    compacting,
    cleaningBackup,
    canCompact,
    canDeleteBackup,
    selectDatabaseDetails,
    summarizeDatabaseDetails,
    describeMaintenanceError,
    compactWorkspace,
    deleteWorkspaceBackup,
  };
}

/** 存储与日志面板的展示口径：纯函数集中于此，便于组件复用与单元测试。 */
import type { StorageValidationResult } from '@shared/bridge/protocol/base';

export type StorageHealth = 'healthy' | 'degraded' | 'readonly';

export interface DiskUsageView {
  usedPercent: number;
  usedGbText: string;
  totalGbText: string;
  freeGbText: string;
  capacityUnknown: boolean;
}

/** 磁盘占用率：总量为 0（网络盘/权限受限）时按“容量未知”兜底，避免除零与 NaN。 */
export function computeDiskUsage(totalMb: number, freeMb: number): DiskUsageView {
  const total = Math.max(0, Number.isFinite(totalMb) ? totalMb : 0);
  const free = Math.max(0, Number.isFinite(freeMb) ? freeMb : 0);
  const capacityUnknown = total <= 0;
  const usedPercent = capacityUnknown ? 0 : Math.max(0, Math.min(100, Math.round(((total - free) / total) * 100)));
  return {
    usedPercent,
    usedGbText: (Math.max(0, total - free) / 1024).toFixed(1),
    totalGbText: (total / 1024).toFixed(1),
    freeGbText: (free / 1024).toFixed(1),
    capacityUnknown,
  };
}

/** UTC 时间字符串转本地化展示；缺失/非法时给出明确文案，不用 0 字节误导。 */
export function formatLastWrite(utc?: string | null): string {
  if (!utc) return '尚未写入';
  const date = new Date(utc);
  return Number.isNaN(date.getTime()) ? '未知' : date.toLocaleString('zh-CN');
}

/** 三态健康度：只读优先于降级，二者皆正常才为健康。 */
export function computeStorageHealth(isWritable: boolean | undefined, isUsingFallback: boolean | undefined): StorageHealth {
  if (isWritable === false) return 'readonly';
  if (isUsingFallback === true) return 'degraded';
  return 'healthy';
}

export function healthTagType(health: StorageHealth): 'success' | 'warning' | 'error' {
  return health === 'readonly' ? 'error' : health === 'degraded' ? 'warning' : 'success';
}

export function healthLabel(health: StorageHealth): string {
  return health === 'readonly' ? '权限受限' : health === 'degraded' ? '降级兜底中' : '健康';
}

/** 0 字节且文件存在 → 空文件，避免 success 绿标误导为正常写入。 */
export function isLogChannelEmpty(exists: boolean, sizeBytes: number): boolean {
  return exists && sizeBytes === 0;
}

/** 事件时间戳格式化；非法时间戳回退为短横线。 */
export function formatEventTime(timestamp: number): string {
  if (!Number.isFinite(timestamp) || timestamp <= 0) return '-';
  const date = new Date(timestamp);
  return Number.isNaN(date.getTime()) ? '-' : date.toLocaleString('zh-CN');
}

/**
 * 迁移确认按钮可用性：预检未返回时不阻塞（兼容老宿主），返回无效 / 空间不足时禁用。
 */
export function canConfirmStorageMigration(targetPath: string, validating: boolean, validation: StorageValidationResult | null): boolean {
  if (validating || !targetPath.trim()) return false;
  if (!validation) return true;
  if (!validation.isValid) return false;
  if (validation.riskCode === 'INSUFFICIENT_FOR_MIGRATE') return false;
  if (validation.requiredSpaceMb > 0 && validation.requiredSpaceMb > validation.availableFreeSpaceMb) return false;
  return true;
}

/** 内存事件类型 → 时间线样式与标题。 */
export function eventDisplayMeta(type: string): { timelineType: 'success' | 'info'; title: string } {
  return type === 'storage:changed' ? { timelineType: 'success', title: '存储路径变更' } : { timelineType: 'info', title: '迁移进度' };
}

/** 持久化事件 payload → 一行摘要（容错各类缺口字段）。 */
export function describeStorageEvent(type: string, payload: Record<string, unknown> | null): string {
  const p = payload ?? {};
  if (type === 'storage:changed') {
    const oldPath = typeof p.oldDataRootPath === 'string' ? p.oldDataRootPath : '?';
    const newPath = typeof p.newDataRootPath === 'string' ? p.newDataRootPath : '?';
    const count = typeof p.migratedFileCount === 'number' ? p.migratedFileCount : 0;
    return `${oldPath} -> ${newPath} (${count} files)`;
  }
  const percent = typeof p.percent === 'number' ? p.percent : 0;
  const msg = typeof p.message === 'string' ? p.message : '迁移进度更新';
  return `[${percent}%] ${msg}`;
}

/** 附件清理报告（后端内嵌 JSON 字符串）解析口径；坏 JSON 回退空报告，不抛给 UI。 */
export interface AttachmentReportView {
  scannedFiles: number;
  scannedBytes: number;
  expiredFiles: number;
  expiredBytes: number;
  deletedFiles: number;
  dryRun: boolean;
  retentionDays: number;
}

export function parseAttachmentReport(json: string | null | undefined): AttachmentReportView {
  const empty: AttachmentReportView = {
    scannedFiles: 0,
    scannedBytes: 0,
    expiredFiles: 0,
    expiredBytes: 0,
    deletedFiles: 0,
    dryRun: true,
    retentionDays: 0,
  };
  if (!json) return empty;
  try {
    const raw = JSON.parse(json) as Partial<AttachmentReportView>;
    return {
      scannedFiles: Number(raw.scannedFiles) || 0,
      scannedBytes: Number(raw.scannedBytes) || 0,
      expiredFiles: Number(raw.expiredFiles) || 0,
      expiredBytes: Number(raw.expiredBytes) || 0,
      deletedFiles: Number(raw.deletedFiles) || 0,
      dryRun: raw.dryRun !== false,
      retentionDays: Number(raw.retentionDays) || 0,
    };
  } catch {
    // 报告仅作展示，坏数据按空报告处理，不打断清理主流程。
    return empty;
  }
}

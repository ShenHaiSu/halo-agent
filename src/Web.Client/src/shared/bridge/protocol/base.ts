/** need21-F02: 桥协议类型（单源：旧 src/bridge/types.ts 按域拆分，行为不变）。 */
/** need22-P5 J4：以下全部自 hostBridge.ts 迁入（DTO + HostBridgeApi + Window 全局 + normalizeVisionFlag），行为不变。 */
export interface BridgeResponse<T> {
  ok: boolean;
  code?: string;
  message?: string;
  data?: T;
  /** 回滚失败残留的会话 ID（可选，仅历史操作失败时出现）。 */
  newSessionId?: string;
  /** 失败阶段（可选，仅历史操作失败时出现）。 */
  failedStage?: string;
}

export interface SystemRuntimeInfo {
  osDescription: string;
  osVersionString: string;
  isWin7: boolean;
  architecture: string;
  processArchitecture: string;
  dotnetVersion: string;
  cefSharpVersion: string;
  currentDataRoot: string;
  bootstrapFilePath: string;
  logsDirectory: string;
  gpuAccelerationDisabled: boolean;
  tlsProtocols: string;
}

export interface LogChannelStatus {
  channel: string;
  label: string;
  fileName: string;
  filePath: string;
  sizeBytes: number;
  exists: boolean;
  lastWriteTimeUtc?: string | null;
}

export interface StorageConfigInfo {
  dataRootPath: string;
  defaultPath: string;
  fallbackPath: string;
  bootstrapFilePath: string;
  previousDataRootPath?: string | null;
  lastModifiedUtc: string;
  isUsingFallback: boolean;
  isWritable: boolean;
  diskFreeSpaceMb: number;
  diskTotalSpaceMb: number;
  logRetentionDays: number;
  logChannels: LogChannelStatus[];
}

export interface StorageValidationResult {
  isValid: boolean;
  normalizedPath: string;
  reasonCode?: string | null;
  message: string;
  availableFreeSpaceMb: number;
  requiredSpaceMb: number;
  targetNonEmpty: boolean;
  riskCode?: string | null;
}

/** 单个受管子目录的存储占用明细。sizeBytes 始终是物理字节总和。 */
export interface StorageBreakdownItem {
  name: string;
  sizeBytes: number;
  fileCount: number;
  /** 仅 sessions 行填充；缺失时老界面忽略。 */
  databaseDetails?: WorkspaceDatabaseStorageDetail[] | null;
}

/** 单个工作区历史库的页级存储明细：逻辑占用与物理文件的双口径对照。 */
export interface WorkspaceDatabaseStorageDetail {
  workspaceId: string;
  databasePath: string;
  physicalBytes: number;
  pageSizeBytes: number;
  pageCount: number;
  freePageCount: number;
  usedPageBytes: number;
  reclaimablePageBytes: number;
  backupBytes: number;
  walBytes: number;
  shmBytes: number;
  tempBytes: number;
  measured: boolean;
  warning?: string | null;
  measuredAtUtc: string;
}

/** 手动压实结果：权威写失败绝不报成功。 */
export interface WorkspaceCompactionResult {
  success: boolean;
  errorCode?: string | null;
  beforeBytes: number;
  afterBytes: number;
  reclaimedBytes: number;
  walCheckpointed: boolean;
  warning?: string | null;
}

/** 备份清理结果：只删备份与残留临时文件。 */
export interface WorkspaceBackupCleanupResult {
  success: boolean;
  errorCode?: string | null;
  freedBytes: number;
  backupExisted: boolean;
  warning?: string | null;
}

/** 数据根目录按子目录聚合的存储分布。 */
export interface StorageBreakdown {
  rootPath: string;
  totalBytes: number;
  items: StorageBreakdownItem[];
}

/** 持久化的存储事件记录（后端 jsonl 每行结构）。 */
export interface StorageEventDto {
  type: string;
  payload: Record<string, unknown> | null;
  timestamp: number;
}

export interface StorageMigrationResult {
  Success?: boolean;
  success: boolean;
  oldDataRootPath: string;
  newDataRootPath: string;
  migratedData: boolean;
  migratedFileCount: number;
  migratedTotalBytes: number;
  message: string;
}

export interface HostEventEnvelope<T = unknown> {
  type: string;
  payload: T;
  timestamp: number;
}

declare global {
  interface Window {
    CefSharp?: {
      BindObjectAsync: (name: string) => Promise<void>;
    };
    hostBridge?: unknown;
    __haloReceiveHostEvents?: (events: Array<HostEventEnvelope>) => void;
    __haloEventBus?: {
      on: <T = unknown>(eventType: string, handler: (payload: T, timestamp: number) => void) => () => void;
      emitLocal: (eventType: string, payload: unknown) => void;
    };
  }
}

/** need21-F02: 桥协议类型（单源：旧 src/bridge/types.ts 按域拆分，行为不变）。 */
/** need22-P5 J4：以下全部自 hostBridge.ts 迁入（DTO + HostBridgeApi + Window 全局 + normalizeVisionFlag），行为不变。 */
export interface BridgeResponse<T> {
  ok: boolean;
  code?: string;
  message?: string;
  data?: T;
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

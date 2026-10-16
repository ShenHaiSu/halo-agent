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

// =========================================================================
// Stage 02: 全局网络代理中枢 (ProxyHub) 与多服务商配置引擎模型定义
// =========================================================================

export type GlobalProxyMode = 'System' | 'Custom' | 'None';
export type ProviderProxyMode = 'Inherit' | 'None' | 'Custom';
export type LlmProtocolType = 'chat' | 'resp';

export interface ManualProxyConfig {
  scheme: string;
  host: string;
  port: number;
  requiresAuth: boolean;
  username?: string | null;
  passwordEncrypted?: string | null;
  password?: string | null;
  passwordMasked?: string | null;
  bypassList: string;
  bypassOnLocal: boolean;
}

export interface GlobalNetworkSettings {
  mode: GlobalProxyMode;
  customProxy: ManualProxyConfig;
  defaultConnectTimeoutSeconds: number;
  defaultRequestTimeoutSeconds: number;
}

export interface SystemProxySnapshot {
  isEnabled: boolean;
  proxyServerRaw?: string | null;
  effectiveHttpProxyUri?: string | null;
  autoConfigUrl?: string | null;
  bypassOverride?: string | null;
  inspectedAtUtc: string;
}

export interface ProxyConnectivityTestResult {
  success: boolean;
  statusCode?: number | null;
  elapsedMs: number;
  effectiveProxyUsed: string;
  targetUrl: string;
  errorMessage?: string | null;
  diagnosticCategory: string;
}

export interface ModelConfig {
  id: string;
  displayName: string;
  protocol: LlmProtocolType;
  maxContextTokens: number;
  maxOutputTokens: number;
  temperature?: number | null;
  enabled: boolean;
}

export interface CustomHeaderEntry {
  key: string;
  valueTemplate: string;
  enabled: boolean;
  isSecret: boolean;
}

export interface ProviderConfig {
  id: string;
  name: string;
  enabled: boolean;
  baseUrl: string;
  apiKeyEncrypted: string;
  apiKey?: string | null;
  apiKeyMasked?: string | null;
  hasApiKey?: boolean | null;
  proxyMode: ProviderProxyMode;
  customProxy?: ManualProxyConfig | null;
  enableCustomHeaders: boolean;
  customHeaders: CustomHeaderEntry[];
  models: ModelConfig[];
}

export interface ProvidersConfigDocument {
  activeProviderId: string;
  activeModelId: string;
  providers: ProviderConfig[];
}

export interface ProviderEndpointPreviewDto {
  baseUrl: string;
  normalizedVersionPrefix: string;
  chatEndpointUrl: string;
  respEndpointUrl: string;
  modelsEndpointUrl: string;
  effectiveProxySummary: string;
  sessionId: string;
  sessionHash: string;
  workspaceName: string;
  workspaceHash: string;
  resolvedHeaders: Record<string, string>;
}

// =========================================================================
// Stage 03: LLM 网关统一请求模型与流式事件 DTO
// =========================================================================

export type UnifiedStreamEventKind =
  | 'StreamStarted'
  | 'ReasoningDelta'
  | 'TextDelta'
  | 'ToolCallStarted'
  | 'ToolCallArgsDelta'
  | 'ToolCallCompleted'
  | 'UsageUpdated'
  | 'StreamCompleted'
  | 'StreamCancelled'
  | 'StreamError';

export interface UnifiedToolCallDto {
  callId: string;
  itemId?: string | null;
  functionName: string;
  argumentsJson: string;
}

export interface UnifiedTokenUsageDto {
  inputTokens: number;
  outputTokens: number;
  cachedInputTokens: number;
  totalTokens: number;
  cacheHitRate?: number;
}

export interface SessionStreamEventDto {
  sessionId: string;
  kind: UnifiedStreamEventKind | string;
  textDelta?: string | null;
  toolCall?: UnifiedToolCallDto | null;
  usage?: UnifiedTokenUsageDto | null;
  errorCode?: string | null;
  errorMessage?: string | null;
}

export interface UnifiedChatMessageDto {
  role: 'system' | 'user' | 'assistant' | 'tool';
  content?: string | null;
  name?: string | null;
  toolCallId?: string | null;
  toolCalls?: Array<{
    id: string;
    type: string;
    function: {
      name: string;
      arguments: string;
    };
  }>;
}

export interface UnifiedToolDefinitionDto {
  name: string;
  description: string;
  parametersJson: string;
}

export interface HeaderResolutionContextDto {
  sessionId: string;
  sessionHash: string;
  workspaceName: string;
  workspacePath: string;
  workspaceHash: string;
  modelId: string;
  providerName: string;
  providerId: string;
  userCustomEnv: string;
}

export interface UnifiedLlmRequestDto {
  requestId: string;
  provider: ProviderConfig;
  model: ModelConfig;
  messages: UnifiedChatMessageDto[];
  systemPrompt?: string;
  temperature?: number;
  maxTokens?: number;
  tools?: UnifiedToolDefinitionDto[];
  headerContext?: HeaderResolutionContextDto;
}

export interface HostEventEnvelope<T = unknown> {
  type: string;
  payload: T;
  timestamp: number;
}

export interface WorkspaceInfo {
  id: string;
  name: string;
  rootPath: string;
  workspaceHash: string;
  defaultProviderId?: string | null;
  defaultModelId?: string | null;
  isPinned: boolean;
  createdAtUtc: string;
  lastOpenedAtUtc: string;
  directoryExists: boolean;
}

export interface SessionMetadata {
  id: string;
  workspaceId: string;
  sessionHash: string;
  title: string;
  providerId?: string | null;
  modelId?: string | null;
  messageCount: number;
  totalInputTokens: number;
  totalOutputTokens: number;
  totalCachedTokens: number;
  cumulativePromptTokens?: number; // need05-01：Σ账单 prompt
  cumulativeCompletionTokens?: number; // need05-01：Σ账单 completion
  isPinned: boolean;
  createdAtUtc: string;
  updatedAtUtc: string;
}

export interface UnifiedChatMessage {
  id: string;
  role: 'system' | 'user' | 'assistant' | 'tool';
  content?: string | null;
  reasoningContent?: string | null;
  toolCalls?: Array<{
    id: string;
    type: string;
    function: {
      name: string;
      arguments: string;
    };
  }> | null;
  toolCallId?: string | null;
  name?: string | null;
  inputTokens?: number | null;
  outputTokens?: number | null;
  cachedTokens?: number | null;
  createdAtUtc: string;
}

export interface ContextBudgetStatusDto {
  totalContextTokens: number;
  maxOutputTokens: number;
  safetyBuffer: number;
  usedInputTokens: number;
  availableInputTokens: number;
  usagePercentage: number;
  isNearLimit: boolean;
  sessionHash: string;
  protocol: string;
   lastPromptTokens?: number | null; // null = unknown（无快照）
   lastCachedTokens?: number | null; // null = unknown；非 null 时恒 ≤ lastPromptTokens
   cacheHitRate?: number | null; // = cached/input；未知则 null（不画 0%）
   usedInputIsEstimated?: boolean; // true = UsedInputTokens 来自估算回退
   occupancyState?: string; // known | unknown | estimated
   billedPromptTokens?: number; // Σ账单 prompt
   billedCompletionTokens?: number;
   // need05-06：本地预测（发送前分块 LCP 推测；实测优先，预测只做未知态兜底）
   predictedCacheHitRate?: number | null;
   predictedCachedTokens?: number | null;
   prefixDivergenceBlock?: string | null;
 }
 
  export interface CompactionResultDto {
    success: boolean;
    errorCode?: string | null;
    errorMessage?: string | null;
    removedMessages: number;
    keptMessages: number;
    summaryPreview: string;
    backupFileName?: string | null;
  }
  export interface PromptConfigDocument {
    version: number; enableHaloPrefix: boolean; enableIdentity: boolean; enableRuntime: boolean;
    enableCmdPython: boolean; enableToolGuide: boolean; enableEphemeral: boolean; enableCustom: boolean;
    customPlacement: 'head' | 'tail'; customText: string; identityText: string; runtimeTemplate: string;
    cmdPythonText: string; toolGuideTemplate: string; ephemeralTemplate: string; scope: string;
    updatedAtUtc: string; isDefault?: boolean; fallbackReason?: string | null;
  }
  // need08：智能体行为配置（config/agent.json；超时毫秒，0=无限）
  export interface AgentConfigDocument {
    version: number; execDefaultTimeoutMs: number; execMaxTimeoutMs: number; maxIterations: number;
    scope: string; updatedAtUtc: string; isDefault?: boolean; fallbackReason?: string | null;
  }
  export interface PromptBlockPreview { blockId: string; enabled: boolean; renderedText: string; estimatedTokens: number; isStable: boolean; }
  export interface PromptPreviewDto {
    assembledText: string; blocks: PromptBlockPreview[]; totalEstimatedTokens: number; stableEstimatedTokens: number;
    prefixHash: string; workspaceId: string; unknownVariables: string[]; isDefault: boolean; fallbackReason?: string | null;
  }
   export interface LastSentPromptSnapshot { reqId: string; sessionId: string; workspaceId: string; prefixHash: string; charLength: number; sentAtUtc: string; }
   export interface PromptDiagnosticsDto {
     currentPreview: PromptPreviewDto; lastSent?: LastSentPromptSnapshot | null; previewEqualsLastSent: boolean;
     compressorPromptPreview: string; titlePromptPreview: string;
   }
   // need07：历史右键（截断/分支）
   export interface HistoryTruncateResultDto {
     success: boolean; errorCode?: string | null; keptMessages: number; removedMessages: number;
     replayText?: string | null; backupFileName?: string | null;
   }

export interface HostBridgeApi {
  // Stage 01
  getSystemInfo: () => Promise<BridgeResponse<SystemRuntimeInfo>>;
  getStorageConfig: () => Promise<BridgeResponse<StorageConfigInfo>>;
  validateStoragePath: (targetPath: string) => Promise<BridgeResponse<StorageValidationResult>>;
  selectFolderDialog: (initialPath?: string) => Promise<BridgeResponse<string | null>>;
  changeStoragePath: (newPath: string, migrateData: boolean) => Promise<BridgeResponse<StorageMigrationResult>>;
  resetStoragePathToDefault: (migrateData: boolean) => Promise<BridgeResponse<StorageMigrationResult>>;
  openPathInExplorer: (targetPath: string) => Promise<BridgeResponse<boolean>>;
  cleanupExpiredLogs: (retentionDays?: number) => Promise<BridgeResponse<number>>;
  writeDiagnosticLog: (channel: string, message: string) => Promise<BridgeResponse<boolean>>;
  setWindowTitle: (title: string) => Promise<BridgeResponse<boolean>>;

  // Stage 02
  getNetworkSettings: () => Promise<BridgeResponse<GlobalNetworkSettings>>;
  saveNetworkSettings: (settings: GlobalNetworkSettings | string) => Promise<BridgeResponse<GlobalNetworkSettings>>;
  inspectSystemProxy: (probeTargetUrl?: string) => Promise<BridgeResponse<SystemProxySnapshot>>;
  testProxyConnectivity: (
    targetUrl?: string,
    overrideSettings?: GlobalNetworkSettings | string | null,
    provider?: ProviderConfig | string | null
  ) => Promise<BridgeResponse<ProxyConnectivityTestResult>>;
  getProvidersConfig: () => Promise<BridgeResponse<ProvidersConfigDocument>>;
  saveProvidersConfig: (doc: ProvidersConfigDocument | string) => Promise<BridgeResponse<ProvidersConfigDocument>>;
  upsertProvider: (provider: ProviderConfig | string) => Promise<BridgeResponse<ProvidersConfigDocument>>;
  deleteProvider: (providerId: string) => Promise<BridgeResponse<ProvidersConfigDocument>>;
  setActiveModel: (providerId: string, modelId: string) => Promise<BridgeResponse<ProvidersConfigDocument>>;
  previewProviderResolution: (
    provider: ProviderConfig | string,
    modelId?: string,
    sessionId?: string,
    workspacePath?: string
  ) => Promise<BridgeResponse<ProviderEndpointPreviewDto>>;

  // Stage 03
  startSessionRun: (request: UnifiedLlmRequestDto | string) => Promise<BridgeResponse<string>>;
  cancelSessionRun: (sessionId: string) => Promise<BridgeResponse<boolean>>;

  // Stage 06: Agent ReAct 编排
  startAgentTurn: (
    workspaceId: string,
    sessionId: string,
    userText: string,
    providerId?: string,
    modelId?: string
  ) => Promise<BridgeResponse<string>>;
  cancelAgentTurn: (sessionId: string) => Promise<BridgeResponse<boolean>>;

  // Stage 04: Workspace, Session & Budget
  getWorkspaces: () => Promise<BridgeResponse<WorkspaceInfo[]>>;
  addWorkspace: (rootPath: string, name?: string) => Promise<BridgeResponse<WorkspaceInfo>>;
  removeWorkspace: (workspaceId: string) => Promise<BridgeResponse<boolean>>;
  browseWorkspaceFolder: (initialPath?: string) => Promise<BridgeResponse<string | null>>;
  getSessions: (workspaceId: string) => Promise<BridgeResponse<SessionMetadata[]>>;
  createSession: (workspaceId: string, title?: string, providerId?: string, modelId?: string) => Promise<BridgeResponse<SessionMetadata>>;
  getSessionMessages: (workspaceId: string, sessionId: string) => Promise<BridgeResponse<UnifiedChatMessage[]>>;
  appendSessionMessage: (workspaceId: string, sessionId: string, message: UnifiedChatMessage | string) => Promise<BridgeResponse<boolean>>;
  updateSessionTitle: (workspaceId: string, sessionId: string, title: string) => Promise<BridgeResponse<boolean>>;
  generateSessionTitle: (workspaceId: string, sessionId: string, providerId?: string, modelId?: string) => Promise<BridgeResponse<string>>;
  setSessionPinned: (workspaceId: string, sessionId: string, isPinned: boolean) => Promise<BridgeResponse<boolean>>;
  deleteSession: (workspaceId: string, sessionId: string) => Promise<BridgeResponse<boolean>>;
  exportSessionMarkdown: (workspaceId: string, sessionId: string) => Promise<BridgeResponse<string>>;
  getContextBudget: (providerId: string, modelId: string, workspaceId: string, sessionId: string) => Promise<BridgeResponse<ContextBudgetStatusDto>>;
  // need05-03: /compact 上下文压缩
  compactSession: (workspaceId: string, sessionId: string, providerId?: string, modelId?: string, keepLastGroups?: number) => Promise<BridgeResponse<CompactionResultDto>>;
  getCompactBackupList: (workspaceId: string, sessionId: string) => Promise<BridgeResponse<string>>;
   // need06: 前置提示词可配化
   getPromptConfig: () => Promise<BridgeResponse<PromptConfigDocument>>;
   savePromptConfig: (config: PromptConfigDocument | string) => Promise<BridgeResponse<PromptConfigDocument>>;
   resetPromptConfig: (blockId?: string) => Promise<BridgeResponse<PromptConfigDocument>>;
   previewPrompt: (workspaceId: string) => Promise<BridgeResponse<PromptPreviewDto>>;
   // need08: 智能体行为配置（命令超时 + 工具轮次可配，0=无限）
   getAgentConfig: () => Promise<BridgeResponse<AgentConfigDocument>>;
   saveAgentConfig: (config: AgentConfigDocument | string) => Promise<BridgeResponse<AgentConfigDocument>>;
   resetAgentConfig: (field?: string) => Promise<BridgeResponse<AgentConfigDocument>>;
   getPromptDiagnostics: (workspaceId: string, sessionId?: string) => Promise<BridgeResponse<PromptDiagnosticsDto>>;
   // need07
   truncateSessionHistory: (workspaceId: string, sessionId: string, anchorMessageId: string, removeAnchor: boolean) => Promise<BridgeResponse<HistoryTruncateResultDto>>;
   branchSession: (workspaceId: string, sourceSessionId: string, anchorMessageId: string, includeAnchor: boolean) => Promise<BridgeResponse<SessionMetadata>>;
}

declare global {
  interface Window {
    CefSharp?: {
      BindObjectAsync: (name: string) => Promise<void>;
    };
    hostBridge?: HostBridgeApi;
    __haloReceiveHostEvents?: (events: Array<HostEventEnvelope>) => void;
    __haloEventBus?: {
      on: <T = unknown>(eventType: string, handler: (payload: T, timestamp: number) => void) => () => void;
      emitLocal: (eventType: string, payload: unknown) => void;
    };
  }
}

type EventListener = (payload: any, timestamp: number) => void;
const listeners = new Map<string, Set<EventListener>>();

function dispatchIncomingEvents(events: Array<HostEventEnvelope>): void {
  if (!Array.isArray(events)) {
    return;
  }

  for (const evt of events) {
    if (!evt || typeof evt.type !== 'string') {
      continue;
    }

    const bucket = listeners.get(evt.type);
    if (bucket) {
      for (const fn of bucket) {
        try {
          fn(evt.payload, evt.timestamp);
        } catch (err) {
          console.error('[haloEventBus] handler error:', err);
        }
      }
    }

    const wildcardBucket = listeners.get('*');
    if (wildcardBucket) {
      for (const fn of wildcardBucket) {
        try {
          fn(evt, evt.timestamp);
        } catch (err) {
          console.error('[haloEventBus] wildcard handler error:', err);
        }
      }
    }
  }
}

export const haloEventBus = {
  on<T = unknown>(eventType: string, handler: (payload: T, timestamp: number) => void): () => void {
    let set = listeners.get(eventType);
    if (!set) {
      set = new Set();
      listeners.set(eventType, set);
    }
    set.add(handler as EventListener);
    return () => {
      set?.delete(handler as EventListener);
    };
  },
  emitLocal(eventType: string, payload: unknown): void {
    dispatchIncomingEvents([
      {
        type: eventType,
        payload,
        timestamp: Date.now(),
      },
    ]);
  },
};

export function normalizeGlobalProxyMode(raw: unknown): GlobalProxyMode {
  if (raw === 1 || String(raw).toLowerCase() === 'custom') return 'Custom';
  if (raw === 2 || String(raw).toLowerCase() === 'none') return 'None';
  return 'System';
}

export function normalizeProviderProxyMode(raw: unknown): ProviderProxyMode {
  if (raw === 1 || String(raw).toLowerCase() === 'none') return 'None';
  if (raw === 2 || String(raw).toLowerCase() === 'custom') return 'Custom';
  return 'Inherit';
}

export function normalizeLlmProtocol(raw: unknown): LlmProtocolType {
  if (raw === 1 || String(raw).toLowerCase() === 'resp' || String(raw).toLowerCase() === 'responses') {
    return 'resp';
  }
  return 'chat';
}

function normalizeNetworkSettingsDto(res: BridgeResponse<GlobalNetworkSettings>): BridgeResponse<GlobalNetworkSettings> {
  if (res.data) {
    res.data.mode = normalizeGlobalProxyMode(res.data.mode);
    if (!res.data.customProxy) {
      res.data.customProxy = {
        scheme: 'http',
        host: '127.0.0.1',
        port: 7890,
        requiresAuth: false,
        bypassList: 'localhost;127.*;10.*;192.168.*',
        bypassOnLocal: true,
      };
    }
  }
  return res;
}

function normalizeProvidersDocDto(res: BridgeResponse<ProvidersConfigDocument>): BridgeResponse<ProvidersConfigDocument> {
  if (res.data && Array.isArray(res.data.providers)) {
    res.data.providers = res.data.providers.map((p) => ({
      ...p,
      proxyMode: normalizeProviderProxyMode(p.proxyMode),
      customHeaders: Array.isArray(p.customHeaders) ? p.customHeaders : [],
      models: Array.isArray(p.models)
        ? p.models.map((m) => ({
            ...m,
            protocol: normalizeLlmProtocol(m.protocol),
          }))
        : [],
    }));
  }
  return res;
}

function normalizeVersionedBaseUrlClient(baseUrl: string): string {
  const trimmed = (baseUrl || 'https://api.openai.com/v1').trim();
  const withScheme = trimmed.includes('://') ? trimmed : `https://${trimmed}`;
  if (withScheme.endsWith('#')) {
    return withScheme.slice(0, -1).replace(/\/+$/, '');
  }
  const clean = withScheme.replace(/\/+$/, '');
  if (clean.toLowerCase().endsWith('/v1')) {
    return clean;
  }
  return `${clean}/v1`;
}

function createBrowserMockBridge(): HostBridgeApi {
  let mockDataRoot = 'C:\\HaloAgentData';
  const today = new Date().toISOString().slice(0, 10).replace(/-/g, '');

  let mockWorkspaces: WorkspaceInfo[] = [
    {
      id: 'ws_demo_default',
      name: 'halo-agent',
      rootPath: 'F:\\MyProgram\\CSharp\\halo-agent',
      workspaceHash: 'a1b2c3d4e5f60718',
      defaultProviderId: 'provider-openai',
      defaultModelId: 'gpt-4o',
      isPinned: false,
      createdAtUtc: new Date().toISOString(),
      lastOpenedAtUtc: new Date().toISOString(),
      directoryExists: true,
    },
  ];

  let mockSessions: Record<string, SessionMetadata[]> = {
    ws_demo_default: [
      {
        id: `sess_${today}_init01`,
        workspaceId: 'ws_demo_default',
        sessionHash: 'f0e1d2c3b4a59687',
        title: '新建会话',
        providerId: 'provider-openai',
        modelId: 'gpt-4o',
        messageCount: 0,
        totalInputTokens: 0,
        totalOutputTokens: 0,
        totalCachedTokens: 0,
        isPinned: false,
        createdAtUtc: new Date().toISOString(),
        updatedAtUtc: new Date().toISOString(),
      },
    ],
  };

  let mockMessages: Record<string, UnifiedChatMessage[]> = {
    [`sess_${today}_init01`]: [],
  };

  let mockNetworkSettings: GlobalNetworkSettings = {
    mode: 'System',
    customProxy: {
      scheme: 'http',
      host: '127.0.0.1',
      port: 7890,
      requiresAuth: false,
      username: '',
      passwordEncrypted: '',
      passwordMasked: '',
      bypassList: 'localhost;127.*;10.*;192.168.*',
      bypassOnLocal: true,
    },
    defaultConnectTimeoutSeconds: 15,
    defaultRequestTimeoutSeconds: 300,
  };

  let mockProvidersDoc: ProvidersConfigDocument = {
    activeProviderId: 'provider-openai',
    activeModelId: 'gpt-4o',
    providers: [
      {
        id: 'provider-openai',
        name: 'OpenAI / 兼容网关',
        enabled: true,
        baseUrl: 'https://api.openai.com/v1',
        apiKeyEncrypted: 'DPAPI_MOCK_CIPHER_1',
        apiKeyMasked: 'sk-p****9876',
        hasApiKey: true,
        proxyMode: 'Inherit',
        customProxy: null,
        enableCustomHeaders: true,
        customHeaders: [
          {
            key: 'X-Session-Hash',
            valueTemplate: '${session-hash}',
            enabled: true,
            isSecret: false,
          },
          {
            key: 'x-opencode-session',
            valueTemplate: '${session-hash}',
            enabled: true,
            isSecret: false,
          },
          {
            key: 'X-Client-Workspace',
            valueTemplate: '${workspace-name}',
            enabled: true,
            isSecret: false,
          },
        ],
        models: [
          {
            id: 'gpt-4o',
            displayName: 'GPT-4o (Chat Completions)',
            protocol: 'chat',
            maxContextTokens: 128000,
            maxOutputTokens: 4096,
            temperature: 0.2,
            enabled: true,
          },
          {
            id: 'o3-mini',
            displayName: 'o3-mini (Responses API)',
            protocol: 'resp',
            maxContextTokens: 200000,
            maxOutputTokens: 16384,
            temperature: null,
            enabled: true,
          },
        ],
      },
      {
        id: 'provider-deepseek',
        name: 'DeepSeek Official',
        enabled: true,
        baseUrl: 'https://api.deepseek.com/v1',
        apiKeyEncrypted: '',
        apiKeyMasked: '',
        hasApiKey: false,
        proxyMode: 'Inherit',
        customProxy: null,
        enableCustomHeaders: false,
        customHeaders: [],
        models: [
          {
            id: 'deepseek-chat',
            displayName: 'DeepSeek-V3 (Chat)',
            protocol: 'chat',
            maxContextTokens: 64000,
            maxOutputTokens: 8192,
            temperature: 0.1,
            enabled: true,
          },
        ],
      },
    ],
   };
   const mockPromptDefaults: PromptConfigDocument = {
     version: 1, enableHaloPrefix: true, enableIdentity: true, enableRuntime: true,
     enableCmdPython: true, enableToolGuide: true, enableEphemeral: true, enableCustom: false,
     customPlacement: 'tail', customText: '',
     identityText: 'You are Halo-Agent, an expert software engineering and system automation AI agent running natively on Windows.',
     runtimeTemplate: '## 1. Runtime & Environment Facts\n- Host OS: {{OsVersion}} (Architecture: {{OsArch}})\n- Active Workspace Name: {{WorkspaceName}}\n- Active Workspace Root Path: {{WorkspaceRoot}}\n- Default Command Shell: `cmd.exe` (Windows Command Prompt)',
     cmdPythonText: '## 2. Command Execution Strategy (CRITICAL RULE)\nBecause PowerShell versions vary drastically, default to cmd.exe first, python second. AVOID powershell unless asked.',
     toolGuideTemplate: '## 3. Standard Tool Usage Guidelines\n- Read before modifying.',
     ephemeralTemplate: '## 4. Session Ephemeral (只供模型参考,不参与前缀命中)\n- Current Date: {{CurrentDate}}\n- Detected Python Runtime: {{PythonStatus}}',
     scope: 'global', updatedAtUtc: new Date().toISOString(),
   };
   let mockPromptConfig: PromptConfigDocument = { ...mockPromptDefaults };
   // need08 mock 仅示意，联调以真后端为准
   const mockAgentDefaults: AgentConfigDocument = {
     version: 1, execDefaultTimeoutMs: 30000, execMaxTimeoutMs: 300000, maxIterations: 25,
     scope: 'global', updatedAtUtc: new Date().toISOString(),
   };
   let mockAgentConfig: AgentConfigDocument = { ...mockAgentDefaults };
   const estimateMockTokens = (t: string): number => {
     if (!t) return 0;
     let ascii = 0; let cjk = 0; let other = 0;
     for (const ch of t) { const c = ch.codePointAt(0) || 0; if (c <= 127) ascii++; else if (c >= 0x4e00 && c <= 0x9fff) cjk++; else other++; }
     return Math.max(1, Math.ceil(ascii / 3.6 + cjk * 1.55 + other * 1.2));
   };
   const buildMockPreview = (cfg: PromptConfigDocument, workspaceId: string): PromptPreviewDto => {
     if (!cfg.enableHaloPrefix) {
       return { assembledText: '', blocks: [], totalEstimatedTokens: 0, stableEstimatedTokens: 0, prefixHash: 'mock', workspaceId, unknownVariables: [], isDefault: false };
     }
     const parts: Array<{ blockId: string; text: string; isStable: boolean }> = [];
     if (cfg.enableCustom && cfg.customText.trim() && cfg.customPlacement === 'head') parts.push({ blockId: 'custom', text: cfg.customText.trim(), isStable: true });
     if (cfg.enableIdentity) parts.push({ blockId: 'identity', text: cfg.identityText.trim(), isStable: true });
     if (cfg.enableRuntime) parts.push({ blockId: 'runtime', text: cfg.runtimeTemplate.trim(), isStable: true });
     if (cfg.enableCmdPython) parts.push({ blockId: 'cmdPython', text: cfg.cmdPythonText.trim(), isStable: true });
     if (cfg.enableToolGuide) parts.push({ blockId: 'toolGuide', text: cfg.toolGuideTemplate.trim(), isStable: true });
     if (cfg.enableEphemeral) parts.push({ blockId: 'ephemeral', text: cfg.ephemeralTemplate.trim(), isStable: false });
     if (cfg.enableCustom && cfg.customText.trim() && cfg.customPlacement === 'tail') parts.push({ blockId: 'custom', text: cfg.customText.trim(), isStable: false });
     const assembled = parts.length ? parts.map((p) => p.text).join('\n\n') + '\n' : '';
     const blocks: PromptBlockPreview[] = parts.map((p) => ({ blockId: p.blockId, enabled: true, renderedText: p.text, estimatedTokens: estimateMockTokens(p.text), isStable: p.isStable }));
     const stable = blocks.filter((b) => b.isStable).reduce((a, b) => a + b.estimatedTokens, 0);
     return { assembledText: assembled, blocks, totalEstimatedTokens: estimateMockTokens(assembled), stableEstimatedTokens: stable, prefixHash: 'mock', workspaceId, unknownVariables: [], isDefault: false };
   };
   return {
    async getSystemInfo() {
      return {
        ok: true,
        data: {
          osDescription: 'Windows 10/11 (Web Dev Mock)',
          osVersionString: 'Microsoft Windows NT 10.0.22631.0',
          isWin7: false,
          architecture: 'X64',
          processArchitecture: 'X64',
          dotnetVersion: '.NET 6.0.36 (Mock)',
          cefSharpVersion: '109.1.110.0',
          currentDataRoot: mockDataRoot,
          bootstrapFilePath: 'C:\\Users\\Developer\\AppData\\Local\\HaloAgent\\bootstrap.json',
          logsDirectory: `${mockDataRoot}\\logs`,
          gpuAccelerationDisabled: false,
          tlsProtocols: 'Tls12, Tls13',
        },
      };
    },
    async getStorageConfig() {
      return {
        ok: true,
        data: {
          dataRootPath: mockDataRoot,
          defaultPath: 'C:\\HaloAgentData',
          fallbackPath: 'C:\\Users\\Developer\\AppData\\Local\\HaloAgent\\Data',
          bootstrapFilePath: 'C:\\Users\\Developer\\AppData\\Local\\HaloAgent\\bootstrap.json',
          previousDataRootPath: null,
          lastModifiedUtc: new Date().toISOString(),
          isUsingFallback: false,
          isWritable: true,
          diskFreeSpaceMb: 131480,
          diskTotalSpaceMb: 512000,
          logRetentionDays: 14,
          logChannels: [
            {
              channel: 'app',
              label: '系统运行日志 (App)',
              fileName: `app-${today}.log`,
              filePath: `${mockDataRoot}\\logs\\app-${today}.log`,
              sizeBytes: 4096,
              exists: true,
              lastWriteTimeUtc: new Date().toISOString(),
            },
            {
              channel: 'llm',
              label: '大模型请求审计 (LLM Trace)',
              fileName: `llm-trace-${today}.log`,
              filePath: `${mockDataRoot}\\logs\\llm-trace-${today}.log`,
              sizeBytes: 1024,
              exists: true,
              lastWriteTimeUtc: new Date().toISOString(),
            },
            {
              channel: 'tool',
              label: '工具执行日志 (Tool Exec)',
              fileName: `tool-exec-${today}.log`,
              filePath: `${mockDataRoot}\\logs\\tool-exec-${today}.log`,
              sizeBytes: 512,
              exists: true,
              lastWriteTimeUtc: new Date().toISOString(),
            },
          ],
        },
      };
    },
    async validateStoragePath(targetPath: string) {
      return {
        ok: true,
        data: {
          isValid: !!targetPath.trim(),
          normalizedPath: targetPath.trim(),
          message: '路径校验通过 (Mock)',
          availableFreeSpaceMb: 131480,
        },
      };
    },
    async selectFolderDialog(initialPath?: string) {
      const input = window.prompt('请输入模拟选择的新存储目录路径：', initialPath || 'C:\\HaloAgentData_Test');
      return {
        ok: true,
        data: input ? input.trim() : null,
      };
    },
    async changeStoragePath(newPath: string, migrateData: boolean) {
      const oldRoot = mockDataRoot;
      haloEventBus.emitLocal('storage:migration-progress', {
        percent: 50,
        message: '正在复制配置文件与日志...',
        targetPath: newPath,
      });
      await new Promise((r) => setTimeout(r, 250));
      mockDataRoot = newPath;
      haloEventBus.emitLocal('storage:migration-progress', {
        percent: 100,
        message: '存储路径切换完成。',
        targetPath: newPath,
      });
      return {
        ok: true,
        message: `已切换至 ${newPath}`,
        data: {
          success: true,
          oldDataRootPath: oldRoot,
          newDataRootPath: newPath,
          migratedData: migrateData,
          migratedFileCount: migrateData ? 4 : 0,
          migratedTotalBytes: migrateData ? 5632 : 0,
          message: `已切换至 ${newPath}`,
        },
      };
    },
    async resetStoragePathToDefault(migrateData: boolean) {
      return this.changeStoragePath('C:\\HaloAgentData', migrateData);
    },
    async openPathInExplorer() {
      return { ok: true, data: true };
    },
    async cleanupExpiredLogs() {
      return { ok: true, data: 0, message: '清理完成，共移除 0 个过期日志文件。' };
    },
    async writeDiagnosticLog() {
      return { ok: true, data: true };
    },
    async setWindowTitle(title: string) {
      document.title = title;
      return { ok: true, data: true };
    },

    // Stage 02 Mock Implementations
    async getNetworkSettings() {
      return {
        ok: true,
        data: JSON.parse(JSON.stringify(mockNetworkSettings)),
      };
    },
    async saveNetworkSettings(settings: GlobalNetworkSettings | string) {
      const parsed: GlobalNetworkSettings =
        typeof settings === 'string' ? JSON.parse(settings) : JSON.parse(JSON.stringify(settings));
      if (parsed.customProxy?.password && !parsed.customProxy.password.includes('****')) {
        parsed.customProxy.passwordEncrypted = 'DPAPI_MOCK_PROXY_PWD';
        parsed.customProxy.passwordMasked = '********';
      }
      if (parsed.customProxy) {
        delete parsed.customProxy.password;
      }
      mockNetworkSettings = parsed;
      return {
        ok: true,
        message: '网络与代理设置已保存并立即生效 (Mock)',
        data: JSON.parse(JSON.stringify(mockNetworkSettings)),
      };
    },
    async inspectSystemProxy() {
      return {
        ok: true,
        data: {
          isEnabled: true,
          proxyServerRaw: '127.0.0.1:7890',
          effectiveHttpProxyUri: 'http://127.0.0.1:7890',
          autoConfigUrl: null,
          bypassOverride: 'localhost;127.*;10.*;192.168.*;<local>',
          inspectedAtUtc: new Date().toISOString(),
        },
      };
    },
    async testProxyConnectivity(targetUrl?: string, overrideSettings?: GlobalNetworkSettings | string | null) {
      const parsedSettings: GlobalNetworkSettings = overrideSettings
        ? typeof overrideSettings === 'string'
          ? JSON.parse(overrideSettings)
          : overrideSettings
        : mockNetworkSettings;

      const effectiveSummary =
        parsedSettings.mode === 'None'
          ? 'DIRECT (No Proxy)'
          : parsedSettings.mode === 'Custom'
          ? `CUSTOM (${parsedSettings.customProxy.scheme}://${parsedSettings.customProxy.host}:${parsedSettings.customProxy.port})`
          : 'SYSTEM (http://127.0.0.1:7890)';

      return {
        ok: true,
        message: `连通测试成功 (42 ms · ${effectiveSummary})`,
        data: {
          success: true,
          statusCode: 200,
          elapsedMs: 42,
          effectiveProxyUsed: effectiveSummary,
          targetUrl: targetUrl || 'https://api.openai.com/v1/models',
          errorMessage: null,
          diagnosticCategory: 'OK',
        },
      };
    },
    async getProvidersConfig() {
      return {
        ok: true,
        data: JSON.parse(JSON.stringify(mockProvidersDoc)),
      };
    },
    async saveProvidersConfig(doc: ProvidersConfigDocument | string) {
      const parsed: ProvidersConfigDocument =
        typeof doc === 'string' ? JSON.parse(doc) : JSON.parse(JSON.stringify(doc));
      mockProvidersDoc = parsed;
      return {
        ok: true,
        message: '大模型服务商与模型配置已安全加密落盘 (Mock)',
        data: JSON.parse(JSON.stringify(mockProvidersDoc)),
      };
    },
    async upsertProvider(provider: ProviderConfig | string) {
      const parsed: ProviderConfig =
        typeof provider === 'string' ? JSON.parse(provider) : JSON.parse(JSON.stringify(provider));
      if (parsed.apiKey && !parsed.apiKey.includes('****')) {
        parsed.apiKeyEncrypted = 'DPAPI_MOCK_CIPHER';
        parsed.apiKeyMasked =
          parsed.apiKey.length > 8 ? `${parsed.apiKey.slice(0, 4)}****${parsed.apiKey.slice(-4)}` : '********';
        parsed.hasApiKey = true;
      }
      delete parsed.apiKey;
      const idx = mockProvidersDoc.providers.findIndex((p) => p.id === parsed.id);
      if (idx >= 0) {
        mockProvidersDoc.providers[idx] = parsed;
      } else {
        mockProvidersDoc.providers.push(parsed);
      }
      return {
        ok: true,
        message: `服务商 [${parsed.name}] 已保存 (Mock)`,
        data: JSON.parse(JSON.stringify(mockProvidersDoc)),
      };
    },
    async deleteProvider(providerId: string) {
      mockProvidersDoc.providers = mockProvidersDoc.providers.filter((p) => p.id !== providerId);
      if (mockProvidersDoc.activeProviderId === providerId) {
        mockProvidersDoc.activeProviderId = mockProvidersDoc.providers[0]?.id || '';
        mockProvidersDoc.activeModelId = mockProvidersDoc.providers[0]?.models[0]?.id || '';
      }
      return {
        ok: true,
        message: '服务商已删除 (Mock)',
        data: JSON.parse(JSON.stringify(mockProvidersDoc)),
      };
    },
    async setActiveModel(providerId: string, modelId: string) {
      mockProvidersDoc.activeProviderId = providerId;
      mockProvidersDoc.activeModelId = modelId;
      return {
        ok: true,
        message: `已切换至模型 ${modelId}`,
        data: JSON.parse(JSON.stringify(mockProvidersDoc)),
      };
    },
    async previewProviderResolution(provider: ProviderConfig | string, modelId?: string) {
      const parsed: ProviderConfig =
        typeof provider === 'string' ? JSON.parse(provider) : provider;
      const prefix = normalizeVersionedBaseUrlClient(parsed.baseUrl);
      const sessionHash = '9f86d081884c7d65';
      const resolvedHeaders: Record<string, string> = {};
      if (parsed.enableCustomHeaders && Array.isArray(parsed.customHeaders)) {
        for (const h of parsed.customHeaders) {
          if (h.enabled && h.key.trim()) {
            resolvedHeaders[h.key.trim()] = (h.valueTemplate || '')
              .replace(/\$\{session-hash\}/gi, sessionHash)
              .replace(/\$\{session-id\}/gi, 'sess_stage02_preview')
              .replace(/\$\{workspace-name\}/gi, 'halo-agent')
              .replace(/\$\{workspace-hash\}/gi, 'a1b2c3d4e5f60718')
              .replace(/\$\{model-id\}/gi, modelId || parsed.models[0]?.id || 'gpt-4o')
              .replace(/\$\{provider-name\}/gi, encodeURIComponent(parsed.name))
              .replace(/\$\{provider-id\}/gi, encodeURIComponent(parsed.id));
          }
        }
      }
      return {
        ok: true,
        data: {
          baseUrl: parsed.baseUrl,
          normalizedVersionPrefix: prefix,
          chatEndpointUrl: `${prefix}/chat/completions`,
          respEndpointUrl: `${prefix}/responses`,
          modelsEndpointUrl: `${prefix}/models`,
          effectiveProxySummary:
            parsed.proxyMode === 'None'
              ? 'DIRECT (No Proxy)'
              : parsed.proxyMode === 'Custom' && parsed.customProxy
              ? `CUSTOM (${parsed.customProxy.scheme}://${parsed.customProxy.host}:${parsed.customProxy.port})`
              : 'SYSTEM (http://127.0.0.1:7890)',
          sessionId: 'sess_stage02_preview',
          sessionHash,
          workspaceName: 'halo-agent',
          workspaceHash: 'a1b2c3d4e5f60718',
          resolvedHeaders,
        },
      };
    },
    async startSessionRun(request: UnifiedLlmRequestDto | string) {
      const parsed: UnifiedLlmRequestDto = typeof request === 'string' ? JSON.parse(request) : request;
      const sessionId = parsed.headerContext?.sessionId || parsed.requestId || `mock_sess_${Date.now()}`;

      // 模拟异步流式输出，验证 Mock 模式下 UI 渲染
      setTimeout(() => {
        haloEventBus.emitLocal('llm:stream-event', {
          sessionId,
          kind: 'StreamStarted',
        });
        haloEventBus.emitLocal('llm:stream-event', {
          sessionId,
          kind: 'TextDelta',
          textDelta: '你好！我是 Halo Agent（测试 Mock 流式模式）。',
        });
        haloEventBus.emitLocal('llm:stream-event', {
          sessionId,
          kind: 'UsageUpdated',
          usage: {
            inputTokens: 120,
            outputTokens: 25,
            cachedInputTokens: 80,
            totalTokens: 145,
          },
        });
        haloEventBus.emitLocal('llm:stream-event', {
          sessionId,
          kind: 'StreamCompleted',
        });
      }, 50);

      return {
        ok: true,
        message: '流式请求已启动 (Mock)',
        data: sessionId,
      };
    },
    async cancelSessionRun(_sessionId: string) {
      return {
        ok: true,
        message: '已发送取消指令 (Mock)',
        data: true,
      };
    },
    async startAgentTurn(workspaceId: string, sessionId: string, userText: string) {
      void workspaceId;
      void userText;
      // 模拟一轮 ReAct：思考增量 -> 文本增量 -> 工具卡片事件 -> 完成
      setTimeout(() => {
        haloEventBus.emitLocal('agent:turn-event', {
          sessionId,
          kind: 'IterationStarted',
          payload: { iteration: 1, maxIterations: 25 },
        });
        haloEventBus.emitLocal('llm:stream-event', {
          sessionId,
          kind: 'ReasoningDelta',
          textDelta: '正在分析任务，准备调用工具...',
        });
        haloEventBus.emitLocal('llm:stream-event', {
          sessionId,
          kind: 'TextDelta',
          textDelta: '收到任务（Mock 演示模式），正在编排工具调用。',
        });
        haloEventBus.emitLocal('agent:turn-event', {
          sessionId,
          kind: 'ToolCallStarted',
          payload: { iteration: 1, callId: 'mock_call_001', functionName: 'list_dir' },
        });
        haloEventBus.emitLocal('agent:turn-event', {
          sessionId,
          kind: 'ToolExecutionCompleted',
          payload: { iteration: 1, callId: 'mock_call_001', functionName: 'list_dir', success: true, exitCode: 0, durationMs: 8 },
        });
        haloEventBus.emitLocal('agent:turn-event', {
          sessionId,
          kind: 'TurnCompleted',
          payload: { iterations: 1, toolExecutions: 1, protocol: 'chat', inputTokens: 120, outputTokens: 25, cachedTokens: 80 },
        });
      }, 60);

      return {
        ok: true,
        message: 'Agent 推理已启动 (Mock)',
        data: sessionId,
      };
    },
    async cancelAgentTurn(_sessionId: string) {
      return {
        ok: true,
        message: '已发送取消指令 (Mock)',
        data: true,
      };
    },

    // Stage 04 Mock Implementations
    async getWorkspaces() {
      return {
        ok: true,
        data: [...mockWorkspaces],
      };
    },
    async addWorkspace(rootPath: string, name?: string) {
      const cleanPath = (rootPath || '').trim();
      const existing = mockWorkspaces.find((w) => w.rootPath.toLowerCase() === cleanPath.toLowerCase());
      if (existing) {
        if (name && name.trim()) existing.name = name.trim();
        return { ok: true, data: existing };
      }
      const dirName = cleanPath.split(/[\\/]/).filter(Boolean).pop() || 'Workspace';
      const ws: WorkspaceInfo = {
        id: `ws_${Math.random().toString(36).substring(2, 10)}`,
        name: name && name.trim() ? name.trim() : dirName,
        rootPath: cleanPath,
        workspaceHash: 'a1b2c3d4e5f60718',
        defaultProviderId: mockProvidersDoc.activeProviderId,
        defaultModelId: mockProvidersDoc.activeModelId,
        isPinned: false,
        createdAtUtc: new Date().toISOString(),
        lastOpenedAtUtc: new Date().toISOString(),
        directoryExists: true,
      };
      mockWorkspaces.push(ws);
      const defaultSess: SessionMetadata = {
        id: `sess_${today}_${Math.random().toString(36).substring(2, 8)}`,
        workspaceId: ws.id,
        sessionHash: 'f0e1d2c3b4a59687',
        title: '新建会话',
        providerId: ws.defaultProviderId,
        modelId: ws.defaultModelId,
        messageCount: 0,
        totalInputTokens: 0,
        totalOutputTokens: 0,
        totalCachedTokens: 0,
        isPinned: false,
        createdAtUtc: new Date().toISOString(),
        updatedAtUtc: new Date().toISOString(),
      };
      mockSessions[ws.id] = [defaultSess];
      mockMessages[defaultSess.id] = [];
      return { ok: true, data: ws };
    },
    async removeWorkspace(workspaceId: string) {
      mockWorkspaces = mockWorkspaces.filter((w) => w.id !== workspaceId);
      delete mockSessions[workspaceId];
      return { ok: true, data: true };
    },
    async browseWorkspaceFolder(_initialPath?: string) {
      return {
        ok: true,
        data: 'F:\\MyProgram\\CSharp\\halo-agent',
      };
    },
    async getSessions(workspaceId: string) {
      const list = mockSessions[workspaceId] || [];
      return {
        ok: true,
        data: [...list],
      };
    },
    async createSession(workspaceId: string, title?: string, providerId?: string, modelId?: string) {
      const sess: SessionMetadata = {
        id: `sess_${today}_${Math.random().toString(36).substring(2, 8)}`,
        workspaceId,
        sessionHash: Math.random().toString(16).substring(2, 18).padEnd(16, '0'),
        title: title && title.trim() ? title.trim() : '新建会话',
        providerId: providerId || mockProvidersDoc.activeProviderId,
        modelId: modelId || mockProvidersDoc.activeModelId,
        messageCount: 0,
        totalInputTokens: 0,
        totalOutputTokens: 0,
        totalCachedTokens: 0,
        isPinned: false,
        createdAtUtc: new Date().toISOString(),
        updatedAtUtc: new Date().toISOString(),
      };
      if (!mockSessions[workspaceId]) {
        mockSessions[workspaceId] = [];
      }
      mockSessions[workspaceId].unshift(sess);
      mockMessages[sess.id] = [];
      return { ok: true, data: sess };
    },
    async getSessionMessages(_workspaceId: string, sessionId: string) {
      const msgs = mockMessages[sessionId] || [];
      return {
        ok: true,
        data: [...msgs],
      };
    },
    async appendSessionMessage(workspaceId: string, sessionId: string, message: UnifiedChatMessage | string) {
      const msg: UnifiedChatMessage = typeof message === 'string' ? JSON.parse(message) : message;
      if (!mockMessages[sessionId]) {
        mockMessages[sessionId] = [];
      }
      mockMessages[sessionId].push(msg);
      const list = mockSessions[workspaceId] || [];
      const sess = list.find((s) => s.id === sessionId);
      if (sess) {
        sess.messageCount = mockMessages[sessionId].length;
        sess.updatedAtUtc = new Date().toISOString();
        if (msg.role === 'user' && sess.title === '新建会话' && msg.content) {
          const firstLine = String(msg.content).trim().split(/[\r\n]+/).map((s) => s.trim()).filter(Boolean)[0] || '';
          const firstSentence = firstLine.split(/[。！？!?;；]+/).map((s) => s.trim()).filter(Boolean)[0] || firstLine;
          sess.title = [...firstSentence.replace(/\s+/g, '')].slice(0, 5).join('') || '新建会话';
        }
      }
      return { ok: true, data: true };
    },
    async updateSessionTitle(workspaceId: string, sessionId: string, title: string) {
      const list = mockSessions[workspaceId] || [];
      const sess = list.find((s) => s.id === sessionId);
      if (sess) {
        sess.title = title.trim();
        sess.updatedAtUtc = new Date().toISOString();
        return { ok: true, data: true };
      }
      return { ok: false, message: '会话不存在', data: false };
    },
    // need03-03 T2 Mock：固定精炼标题短路，保证 Web 调试同样走两阶段
    async generateSessionTitle(workspaceId: string, sessionId: string) {
      const list = mockSessions[workspaceId] || [];
      const sess = list.find((s) => s.id === sessionId);
      if (!sess) return { ok: false, message: '会话不存在', data: '' };
      const msgs = mockMessages[sessionId] || [];
      const firstAssistant = [...msgs].reverse().find((m) => m.role === 'assistant');
      const src = String(firstAssistant?.content || msgs.find((m) => m.role === 'user')?.content || '');
      const firstSentence = src.trim().split(/[\r\n]+/).map((s) => s.trim()).filter(Boolean)[0]?.split(/[。！？!?;；]+/).map((s) => s.trim()).filter(Boolean)[0] || 'Mock精炼标题';
      const title = [...firstSentence.replace(/\s+/g, '')].slice(0, 6).join('') || 'Mock精炼标题';
      sess.title = title;
      sess.updatedAtUtc = new Date().toISOString();
      return { ok: true, data: title };
    },
    async setSessionPinned(workspaceId: string, sessionId: string, isPinned: boolean) {
      const list = mockSessions[workspaceId] || [];
      const sess = list.find((s) => s.id === sessionId);
      if (sess) {
        sess.isPinned = isPinned;
        sess.updatedAtUtc = new Date().toISOString();
        return { ok: true, data: true };
      }
      return { ok: false, message: '会话不存在', data: false };
    },
    async deleteSession(workspaceId: string, sessionId: string) {
      if (mockSessions[workspaceId]) {
        mockSessions[workspaceId] = mockSessions[workspaceId].filter((s) => s.id !== sessionId);
      }
      delete mockMessages[sessionId];
      return { ok: true, data: true };
    },
    async exportSessionMarkdown(_workspaceId: string, sessionId: string) {
      const msgs = mockMessages[sessionId] || [];
      let md = `# 会话记录 (${sessionId})\n\n`;
      for (const m of msgs) {
        md += `### ${m.role}\n${m.content || ''}\n\n`;
      }
      return { ok: true, data: md };
    },
    async getContextBudget(_providerId: string, _modelId: string, _workspaceId: string, sessionId: string) {
      const msgs = mockMessages[sessionId] || [];
      const used = msgs.reduce((acc, m) => acc + (m.content?.length || 0) * 0.5, 120);
      const rounded = Math.round(used);
      return {
        ok: true,
        data: {
          totalContextTokens: 128000,
          maxOutputTokens: 4096,
          safetyBuffer: 1024,
          usedInputTokens: rounded,
           availableInputTokens: Math.max(0, 128000 - 4096 - 1024 - rounded),
           usagePercentage: Math.round((rounded / 128000) * 1000) / 10,
           isNearLimit: false,
           sessionHash: 'f0e1d2c3b4a59687',
           protocol: 'chat',
           lastPromptTokens: rounded,
           lastCachedTokens: Math.min(rounded, 80),
           cacheHitRate: rounded > 0 ? Math.min(80, rounded) / rounded : null,
           usedInputIsEstimated: false,
           occupancyState: 'estimated',
           billedPromptTokens: 1200,
           billedCompletionTokens: 300,
           // need05-06 mock：本地预测兜底字段
           predictedCacheHitRate: rounded > 0 ? Math.min(80, rounded) / rounded : null,
           predictedCachedTokens: Math.min(rounded, 80),
           prefixDivergenceBlock: 'latest',
        },
      };
    },
    // need05-03 mock：插入一条假摘要 + 事件回放
    async compactSession(_workspaceId: string, sessionId: string) {
      if (!mockMessages[sessionId]) mockMessages[sessionId] = [];
      mockMessages[sessionId].push({
        id: `msg_mock_summary_${Date.now()}`,
        role: 'user',
        content: '【上下文摘要：压缩自此前 2 条，由模型自动生成，细节以备份为准】\n- Mock 要点：任务目标/进展、关键结论、用户偏好、未办事项',
        createdAtUtc: new Date().toISOString(),
      } as UnifiedChatMessage);
      setTimeout(() => {
        haloEventBus.emitLocal('agent:turn-event', { sessionId, kind: 'CompactStarted', payload: { sessionId } });
        haloEventBus.emitLocal('agent:turn-event', {
          sessionId,
          kind: 'CompactCompleted',
          payload: { removedMessages: 2, keptMessages: 4, summaryPreview: 'Mock 要点', updatedAtUtc: new Date().toISOString() },
        });
      }, 60);
      return { ok: true, data: { success: true, removedMessages: 2, keptMessages: 4, summaryPreview: 'Mock 要点' } };
    },
     async getCompactBackupList(_workspaceId: string, _sessionId: string) {
       return { ok: true, data: '[]' };
     },
     // need06 mock 仅示意，联调以真后端为准
     async getPromptConfig() {
       return { ok: true, data: { ...mockPromptConfig } };
     },
     async savePromptConfig(config: PromptConfigDocument | string) {
       const parsed: PromptConfigDocument = typeof config === 'string' ? JSON.parse(config) : JSON.parse(JSON.stringify(config));
       mockPromptConfig = parsed;
       return { ok: true, message: '提示词配置已保存，下一轮对话起生效 (Mock)', data: { ...mockPromptConfig } };
     },
     async resetPromptConfig(blockId?: string) {
       if (!blockId) { mockPromptConfig = { ...mockPromptDefaults }; }
       else if (blockId === 'identity') { mockPromptConfig.identityText = mockPromptDefaults.identityText; mockPromptConfig.enableIdentity = true; }
       else if (blockId === 'runtime') { mockPromptConfig.runtimeTemplate = mockPromptDefaults.runtimeTemplate; mockPromptConfig.enableRuntime = true; }
       else if (blockId === 'cmdPython') { mockPromptConfig.cmdPythonText = mockPromptDefaults.cmdPythonText; mockPromptConfig.enableCmdPython = true; }
       else if (blockId === 'toolGuide') { mockPromptConfig.toolGuideTemplate = mockPromptDefaults.toolGuideTemplate; mockPromptConfig.enableToolGuide = true; }
       else if (blockId === 'ephemeral') { mockPromptConfig.ephemeralTemplate = mockPromptDefaults.ephemeralTemplate; mockPromptConfig.enableEphemeral = true; }
       else if (blockId === 'custom') { mockPromptConfig.customText = ''; mockPromptConfig.enableCustom = false; mockPromptConfig.customPlacement = 'tail'; }
       return { ok: true, message: '已恢复默认 (Mock)', data: { ...mockPromptConfig } };
     },
     // need08 mock 仅示意，联调以真后端为准
     async getAgentConfig() {
       return { ok: true, data: { ...mockAgentConfig } };
     },
     async saveAgentConfig(config: AgentConfigDocument | string) {
       const parsed: AgentConfigDocument = typeof config === 'string' ? JSON.parse(config) : JSON.parse(JSON.stringify(config));
       mockAgentConfig = { ...parsed, updatedAtUtc: new Date().toISOString() };
       return { ok: true, message: '智能体行为配置已保存，下一轮对话起生效 (Mock)', data: { ...mockAgentConfig } };
     },
     async resetAgentConfig(field?: string) {
       const id = (field || '').trim().toLowerCase();
       if (!id || id === 'all' || id === 'default') { mockAgentConfig = { ...mockAgentDefaults }; }
       else if (['defaulttimeout', 'execdefaulttimeoutms', 'default', 'default-timeout', 'default_timeout'].includes(id)) { mockAgentConfig.execDefaultTimeoutMs = mockAgentDefaults.execDefaultTimeoutMs; }
       else if (['maxtimeout', 'execmaxtimeoutms', 'max', 'max-timeout', 'max_timeout'].includes(id)) { mockAgentConfig.execMaxTimeoutMs = mockAgentDefaults.execMaxTimeoutMs; }
       else if (['iterations', 'maxiterations', 'rounds', 'max-iterations', 'max_iterations'].includes(id)) { mockAgentConfig.maxIterations = mockAgentDefaults.maxIterations; }
       else { return { ok: false, code: 'AGENT_VALIDATION', message: `字段 [field]：未知恢复项：${field}` }; }
       mockAgentConfig.updatedAtUtc = new Date().toISOString();
       return { ok: true, message: '已恢复默认 (Mock)', data: { ...mockAgentConfig } };
     },
     async previewPrompt(workspaceId: string) {
       return { ok: true, data: buildMockPreview(mockPromptConfig, workspaceId) };
     },
     async getPromptDiagnostics(workspaceId: string, sessionId?: string) {
       const currentPreview = buildMockPreview(mockPromptConfig, workspaceId);
       return {
         ok: true,
         data: {
           currentPreview,
           lastSent: sessionId ? { reqId: 'mock_req_001', sessionId, workspaceId, prefixHash: currentPreview.prefixHash, charLength: currentPreview.assembledText.length, sentAtUtc: new Date().toISOString() } : null,
           previewEqualsLastSent: true,
           compressorPromptPreview: '你是会话上下文压缩器。把用户提供的对话压缩成精炼的中文要点列表（Mock 预览）。',
           titlePromptPreview: '你是会话标题生成器（Mock 预览）。（对话正文已脱除）',
         },
       };
     },
     // need07 mock：与真后端同语义（轮次展开 + 前驱 user 切面）
     async truncateSessionHistory(_workspaceId: string, sessionId: string, anchorMessageId: string) {
       const msgs = mockMessages[sessionId] || [];
       const idx = msgs.findIndex((m) => String(m.id) === String(anchorMessageId));
       if (idx < 0) return { ok: false, code: 'ANCHOR_NOT_FOUND', message: '历史已变化，请刷新后重试' };
       const role = String(msgs[idx].role || '').toLowerCase();
       let cut = idx;
       let replayText: string | null = null;
       if (role === 'assistant') {
         let j = idx;
         while (j >= 0 && String(msgs[j].role || '').toLowerCase() !== 'user') j--;
         if (j < 0) return { ok: false, code: 'NO_PRIOR_USER', message: '找不到上一条用户消息，无法重放' };
         cut = j; replayText = String(msgs[j].content || '');
       } else {
         replayText = String(msgs[idx].content || '');
       }
       if (!replayText.trim()) return { ok: false, code: 'EMPTY_REPLAY_TEXT', message: '该消息内容为空，无法重放' };
       const kept = msgs.slice(0, cut);
       const removed = msgs.length - kept.length;
       mockMessages[sessionId] = kept;
       return { ok: true, data: { success: true, keptMessages: kept.length, removedMessages: removed, replayText, backupFileName: 'mock-replay-bak' } };
     },
     async branchSession(workspaceId: string, sourceSessionId: string, anchorMessageId: string, includeAnchor: boolean) {
       const msgs = mockMessages[sourceSessionId] || [];
       const idx = msgs.findIndex((m) => String(m.id) === String(anchorMessageId));
       if (idx < 0) return { ok: false, code: 'ANCHOR_NOT_FOUND', message: '历史已变化，请刷新后重试' };
       let end = idx;
       if (String(msgs[idx].role || '').toLowerCase() === 'assistant') {
         let t2 = idx + 1;
         while (t2 < msgs.length && String(msgs[t2].role || '').toLowerCase() === 'tool') t2++;
         end = t2 - 1;
       }
       const prefix = (includeAnchor ? msgs.slice(0, end + 1) : msgs.slice(0, idx)).map((m) => ({ ...m }));
       const res = await (this as unknown as { createSession: (w: string, t?: string) => Promise<{ ok: boolean; data: unknown }> }).createSession(workspaceId, '分支会话');
       const sess = (res as unknown as { data: SessionMetadata }).data;
       mockMessages[sess.id] = prefix;
       const list = mockSessions[workspaceId] || [];
       const hit = list.find((s) => s.id === sess.id);
       if (hit) hit.messageCount = prefix.length;
       return { ok: true, data: sess as SessionMetadata };
     },
  };
}

function toCamelKey(key: string): string {
  if (!key) return key;
  return key.charAt(0).toLowerCase() + key.slice(1);
}

 function normalizeBridgePayload<T>(input: any, preserveDictionaryKeys = false): T {
   // CefSharp 会把 C# DateTime 转成 JS Date（typeof object）。旧实现直接 Object.entries(Date)
   // → {}，导致 updatedAtUtc 回传变成 {"updatedAtUtc":{}}，后端 STJ 报 Unsupported token StartObject。
   // 此处优先还原为 ISO 字符串，保证往返契约始终是 string。
   if (input instanceof Date) {
     return (Number.isNaN(input.getTime()) ? new Date().toISOString() : input.toISOString()) as unknown as T;
   }
   if (input === null || input === undefined || typeof input !== 'object') {
     return input as T;
   }
   if (Array.isArray(input)) {
     return input.map((item) => normalizeBridgePayload(item, false)) as unknown as T;
   }
   const result: Record<string, any> = {};
   for (const [key, value] of Object.entries(input)) {
     const outKey = preserveDictionaryKeys ? key : toCamelKey(key);
     const isStringDictionary = outKey === 'resolvedHeaders';
     result[outKey] = normalizeBridgePayload(value, isStringDictionary);
   }
   return result as T;
 }

 /** need06：保存前把 updatedAtUtc 归一为 ISO 字符串。CefSharp 回传的 Date / 已损坏的 {} / 数字时间戳全部在此消化，后端只收干净 string。 */
 function sanitizePromptConfigForSave(config: PromptConfigDocument | string): string {
   if (typeof config === 'string') {
     return config;
   }
   const raw = config as unknown as Record<string, unknown>;
   const copy: Record<string, unknown> = { ...raw };
   copy.updatedAtUtc = toIsoDateString(raw.updatedAtUtc);
   return JSON.stringify(copy);
 }
/** need08：同 need06 归一逻辑，智能体行为配置保存前把 updatedAtUtc 归一为 ISO 字符串。 */
function sanitizeAgentConfigForSave(config: AgentConfigDocument | string): string {
  if (typeof config === 'string') {
    return config;
  }
  const raw = config as unknown as Record<string, unknown>;
  const copy: Record<string, unknown> = { ...raw };
  copy.updatedAtUtc = toIsoDateString(raw.updatedAtUtc);
  return JSON.stringify(copy);
}

 function toIsoDateString(v: unknown): string {
   if (typeof v === 'string' && v.trim()) {
     const t = Date.parse(v);
     // 能解析就原样保留（后端 STJ 只认 ISO）；解析不了就用现在时间兜底，避免把垃圾发给后端。
     return Number.isNaN(t) ? new Date().toISOString() : v;
   }
   if (v instanceof Date && !Number.isNaN(v.getTime())) {
     return v.toISOString();
   }
   if (typeof v === 'number' && Number.isFinite(v)) {
     // 10 位按秒、13 位按毫秒换算成 ISO
     const ms = v >= 100_000_000_000 ? v : v * 1000;
     const d = new Date(ms);
     return Number.isNaN(d.getTime()) ? new Date().toISOString() : d.toISOString();
   }
   // {} / null / undefined / 其他：直接用当前时间，后端 Save 会再归一为 UtcNow
   return new Date().toISOString();
 }

function wrapNativeHostBridge(rawBridge: any): HostBridgeApi {
  return {
    async getSystemInfo() {
      return normalizeBridgePayload<BridgeResponse<SystemRuntimeInfo>>(await rawBridge.getSystemInfo());
    },
    async getStorageConfig() {
      return normalizeBridgePayload<BridgeResponse<StorageConfigInfo>>(await rawBridge.getStorageConfig());
    },
    async validateStoragePath(targetPath: string) {
      return normalizeBridgePayload<BridgeResponse<StorageValidationResult>>(await rawBridge.validateStoragePath(targetPath));
    },
    async selectFolderDialog(initialPath?: string) {
      return normalizeBridgePayload<BridgeResponse<string | null>>(await rawBridge.selectFolderDialog(initialPath ?? ''));
    },
    async changeStoragePath(newPath: string, migrateData: boolean) {
      return normalizeBridgePayload<BridgeResponse<StorageMigrationResult>>(await rawBridge.changeStoragePath(newPath, migrateData));
    },
    async resetStoragePathToDefault(migrateData: boolean) {
      return normalizeBridgePayload<BridgeResponse<StorageMigrationResult>>(await rawBridge.resetStoragePathToDefault(migrateData));
    },
    async openPathInExplorer(targetPath: string) {
      return normalizeBridgePayload<BridgeResponse<boolean>>(await rawBridge.openPathInExplorer(targetPath));
    },
    async cleanupExpiredLogs(retentionDays = 14) {
      return normalizeBridgePayload<BridgeResponse<number>>(await rawBridge.cleanupExpiredLogs(retentionDays));
    },
    async writeDiagnosticLog(channel: string, message: string) {
      return normalizeBridgePayload<BridgeResponse<boolean>>(await rawBridge.writeDiagnosticLog(channel, message));
    },
    async setWindowTitle(title: string) {
      return normalizeBridgePayload<BridgeResponse<boolean>>(await rawBridge.setWindowTitle(title));
    },

    // Stage 02 Native Bridge Wrappers
    async getNetworkSettings() {
      const res = normalizeBridgePayload<BridgeResponse<GlobalNetworkSettings>>(await rawBridge.getNetworkSettings());
      return normalizeNetworkSettingsDto(res);
    },
    async saveNetworkSettings(settings: GlobalNetworkSettings | string) {
      const payload = typeof settings === 'string' ? settings : JSON.stringify(settings);
      const res = normalizeBridgePayload<BridgeResponse<GlobalNetworkSettings>>(await rawBridge.saveNetworkSettings(payload));
      return normalizeNetworkSettingsDto(res);
    },
    async inspectSystemProxy(probeTargetUrl?: string) {
      return normalizeBridgePayload<BridgeResponse<SystemProxySnapshot>>(
        await rawBridge.inspectSystemProxy(probeTargetUrl ?? 'https://api.openai.com')
      );
    },
    async testProxyConnectivity(
      targetUrl?: string,
      overrideSettings?: GlobalNetworkSettings | string | null,
      provider?: ProviderConfig | string | null
    ) {
      const settingsStr =
        overrideSettings === null || overrideSettings === undefined
          ? ''
          : typeof overrideSettings === 'string'
          ? overrideSettings
          : JSON.stringify(overrideSettings);
      const providerStr =
        provider === null || provider === undefined
          ? ''
          : typeof provider === 'string'
          ? provider
          : JSON.stringify(provider);

      return normalizeBridgePayload<BridgeResponse<ProxyConnectivityTestResult>>(
        await rawBridge.testProxyConnectivity(targetUrl ?? '', settingsStr, providerStr)
      );
    },
    async getProvidersConfig() {
      const res = normalizeBridgePayload<BridgeResponse<ProvidersConfigDocument>>(await rawBridge.getProvidersConfig());
      return normalizeProvidersDocDto(res);
    },
    async saveProvidersConfig(doc: ProvidersConfigDocument | string) {
      const payload = typeof doc === 'string' ? doc : JSON.stringify(doc);
      const res = normalizeBridgePayload<BridgeResponse<ProvidersConfigDocument>>(await rawBridge.saveProvidersConfig(payload));
      return normalizeProvidersDocDto(res);
    },
    async upsertProvider(provider: ProviderConfig | string) {
      const payload = typeof provider === 'string' ? provider : JSON.stringify(provider);
      const res = normalizeBridgePayload<BridgeResponse<ProvidersConfigDocument>>(await rawBridge.upsertProvider(payload));
      return normalizeProvidersDocDto(res);
    },
    async deleteProvider(providerId: string) {
      const res = normalizeBridgePayload<BridgeResponse<ProvidersConfigDocument>>(await rawBridge.deleteProvider(providerId));
      return normalizeProvidersDocDto(res);
    },
    async setActiveModel(providerId: string, modelId: string) {
      const res = normalizeBridgePayload<BridgeResponse<ProvidersConfigDocument>>(
        await rawBridge.setActiveModel(providerId, modelId)
      );
      return normalizeProvidersDocDto(res);
    },
    async previewProviderResolution(
      provider: ProviderConfig | string,
      modelId?: string,
      sessionId?: string,
      workspacePath?: string
    ) {
      const payload = typeof provider === 'string' ? provider : JSON.stringify(provider);
      return normalizeBridgePayload<BridgeResponse<ProviderEndpointPreviewDto>>(
        await rawBridge.previewProviderResolution(payload, modelId ?? '', sessionId ?? '', workspacePath ?? '')
      );
    },
    async startSessionRun(request: UnifiedLlmRequestDto | string) {
      const payload = typeof request === 'string' ? request : JSON.stringify(request);
      return normalizeBridgePayload<BridgeResponse<string>>(await rawBridge.startSessionRun(payload));
    },
    async cancelSessionRun(sessionId: string) {
      return normalizeBridgePayload<BridgeResponse<boolean>>(await rawBridge.cancelSessionRun(sessionId));
    },
    async startAgentTurn(workspaceId: string, sessionId: string, userText: string, providerId?: string, modelId?: string) {
      return normalizeBridgePayload<BridgeResponse<string>>(
        await rawBridge.startAgentTurn(workspaceId, sessionId, userText, providerId ?? '', modelId ?? '')
      );
    },
    async cancelAgentTurn(sessionId: string) {
      return normalizeBridgePayload<BridgeResponse<boolean>>(await rawBridge.cancelAgentTurn(sessionId));
    },

    // Stage 04 Native Wrappers
    async getWorkspaces() {
      return normalizeBridgePayload<BridgeResponse<WorkspaceInfo[]>>(await rawBridge.getWorkspaces());
    },
    async addWorkspace(rootPath: string, name?: string) {
      return normalizeBridgePayload<BridgeResponse<WorkspaceInfo>>(await rawBridge.addWorkspace(rootPath, name ?? ''));
    },
    async removeWorkspace(workspaceId: string) {
      return normalizeBridgePayload<BridgeResponse<boolean>>(await rawBridge.removeWorkspace(workspaceId));
    },
    async browseWorkspaceFolder(initialPath?: string) {
      return normalizeBridgePayload<BridgeResponse<string | null>>(await rawBridge.browseWorkspaceFolder(initialPath ?? ''));
    },
    async getSessions(workspaceId: string) {
      return normalizeBridgePayload<BridgeResponse<SessionMetadata[]>>(await rawBridge.getSessions(workspaceId));
    },
    async createSession(workspaceId: string, title?: string, providerId?: string, modelId?: string) {
      return normalizeBridgePayload<BridgeResponse<SessionMetadata>>(
        await rawBridge.createSession(workspaceId, title ?? '', providerId ?? '', modelId ?? '')
      );
    },
    async getSessionMessages(workspaceId: string, sessionId: string) {
      return normalizeBridgePayload<BridgeResponse<UnifiedChatMessage[]>>(
        await rawBridge.getSessionMessages(workspaceId, sessionId)
      );
    },
    async appendSessionMessage(workspaceId: string, sessionId: string, message: UnifiedChatMessage | string) {
      const payload = typeof message === 'string' ? message : JSON.stringify(message);
      return normalizeBridgePayload<BridgeResponse<boolean>>(
        await rawBridge.appendSessionMessage(workspaceId, sessionId, payload)
      );
    },
    async updateSessionTitle(workspaceId: string, sessionId: string, title: string) {
      return normalizeBridgePayload<BridgeResponse<boolean>>(
        await rawBridge.updateSessionTitle(workspaceId, sessionId, title)
      );
    },
    async generateSessionTitle(workspaceId: string, sessionId: string, providerId?: string, modelId?: string) {
      return normalizeBridgePayload<BridgeResponse<string>>(
        await rawBridge.generateSessionTitle(workspaceId, sessionId, providerId ?? '', modelId ?? '')
      );
    },
    async setSessionPinned(workspaceId: string, sessionId: string, isPinned: boolean) {
      return normalizeBridgePayload<BridgeResponse<boolean>>(
        await rawBridge.setSessionPinned(workspaceId, sessionId, isPinned)
      );
    },
    async deleteSession(workspaceId: string, sessionId: string) {
      return normalizeBridgePayload<BridgeResponse<boolean>>(
        await rawBridge.deleteSession(workspaceId, sessionId)
      );
    },
    async exportSessionMarkdown(workspaceId: string, sessionId: string) {
      return normalizeBridgePayload<BridgeResponse<string>>(
        await rawBridge.exportSessionMarkdown(workspaceId, sessionId)
      );
    },
    async getContextBudget(providerId: string, modelId: string, workspaceId: string, sessionId: string) {
      return normalizeBridgePayload<BridgeResponse<ContextBudgetStatusDto>>(
        await rawBridge.getContextBudget(providerId, modelId, workspaceId, sessionId)
      );
    },
    async compactSession(workspaceId: string, sessionId: string, providerId?: string, modelId?: string, keepLastGroups?: number) {
      return normalizeBridgePayload<BridgeResponse<CompactionResultDto>>(
        await rawBridge.compactSession(workspaceId, sessionId, providerId ?? '', modelId ?? '', keepLastGroups ?? 6)
      );
    },
     async getCompactBackupList(workspaceId: string, sessionId: string) {
       return normalizeBridgePayload<BridgeResponse<string>>(
         await rawBridge.getCompactBackupList(workspaceId, sessionId)
       );
     },
     async getPromptConfig() {
       return normalizeBridgePayload<BridgeResponse<PromptConfigDocument>>(await rawBridge.getPromptConfig());
     },
     async savePromptConfig(config: PromptConfigDocument | string) {
       const payload = sanitizePromptConfigForSave(config);
       return normalizeBridgePayload<BridgeResponse<PromptConfigDocument>>(await rawBridge.savePromptConfig(payload));
     },
     async resetPromptConfig(blockId?: string) {
       return normalizeBridgePayload<BridgeResponse<PromptConfigDocument>>(await rawBridge.resetPromptConfig(blockId ?? ''));
     },
     async getAgentConfig() {
       return normalizeBridgePayload<BridgeResponse<AgentConfigDocument>>(await rawBridge.getAgentConfig());
     },
     async saveAgentConfig(config: AgentConfigDocument | string) {
       const payload = sanitizeAgentConfigForSave(config);
       return normalizeBridgePayload<BridgeResponse<AgentConfigDocument>>(await rawBridge.saveAgentConfig(payload));
     },
     async resetAgentConfig(field?: string) {
       return normalizeBridgePayload<BridgeResponse<AgentConfigDocument>>(await rawBridge.resetAgentConfig(field ?? ''));
     },
     async previewPrompt(workspaceId: string) {
       return normalizeBridgePayload<BridgeResponse<PromptPreviewDto>>(await rawBridge.previewPrompt(workspaceId ?? ''));
     },
     async getPromptDiagnostics(workspaceId: string, sessionId?: string) {
       return normalizeBridgePayload<BridgeResponse<PromptDiagnosticsDto>>(
         await rawBridge.getPromptDiagnostics(workspaceId ?? '', sessionId ?? '')
       );
     },
     async truncateSessionHistory(workspaceId: string, sessionId: string, anchorMessageId: string, removeAnchor: boolean) {
       return normalizeBridgePayload<BridgeResponse<HistoryTruncateResultDto>>(
         await rawBridge.truncateSessionHistory(workspaceId ?? '', sessionId ?? '', anchorMessageId ?? '', removeAnchor ?? true)
       );
     },
     async branchSession(workspaceId: string, sourceSessionId: string, anchorMessageId: string, includeAnchor: boolean) {
       return normalizeBridgePayload<BridgeResponse<SessionMetadata>>(
         await rawBridge.branchSession(workspaceId ?? '', sourceSessionId ?? '', anchorMessageId ?? '', includeAnchor ?? true)
       );
     },
  };
}

let resolvedBridge: HostBridgeApi | null = null;

export async function initHostBridge(): Promise<HostBridgeApi> {
  window.__haloReceiveHostEvents = dispatchIncomingEvents;
  window.__haloEventBus = haloEventBus;

  if (resolvedBridge) {
    return resolvedBridge;
  }

  if (window.CefSharp && !window.hostBridge) {
    try {
      await window.CefSharp.BindObjectAsync('hostBridge');
    } catch (err) {
      console.error('[hostBridge] BindObjectAsync failed:', err);
    }
  }

  if (window.hostBridge) {
    resolvedBridge = wrapNativeHostBridge(window.hostBridge);
  } else {
    resolvedBridge = createBrowserMockBridge();
  }

  window.hostBridge = resolvedBridge;
  return resolvedBridge;
}

export function getHostBridge(): HostBridgeApi {
  if (!resolvedBridge) {
    if (window.hostBridge) {
      resolvedBridge = wrapNativeHostBridge(window.hostBridge);
    } else {
      resolvedBridge = createBrowserMockBridge();
    }
    window.hostBridge = resolvedBridge;
  }
  return resolvedBridge;
}

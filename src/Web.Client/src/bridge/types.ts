/** need22-P3 G5：桥接类型 + 版本 + 错误码（四模块之 types，无依赖，被 client/mock/proxy/hostBridge 引用）。 */

/** 桥接协议版本。proxy negotiate() 握手要求后端版本一致，否则直接抛错、不静默降级。 */
export const BRIDGE_VERSION = 2;

/** Mock 显式横幅文案（红色横幅 + 重试按钮，禁止静默 Mock）。 */
export const MOCK_BANNER_TEXT = '桥接未连接（Mock数据）';

export type BridgeErrorCode =
  | 'BRIDGE_HANDSHAKE_FAILED'
  | 'BRIDGE_NOT_CONNECTED'
  | 'BRIDGE_VERSION_MISMATCH'
  | 'AGENT_START_ERROR'
  | 'ASK_UNSUPPORTED'
  | 'SUB_NOT_FOUND'
  | 'USAGE_UNSUPPORTED'
  | 'TURN_ERROR'
  | 'STREAM_ERROR'
  | 'COMPACT_FAILED'
  | 'SEND_FAILED'
  | 'BUDGET_FAILED'
  | 'REFRESH_FAILED'
  | 'UNKNOWN';

export interface BridgeError {
  code: BridgeErrorCode;
  message: string;
}

/** 统一失败体 {code,message} + toast + 可选重试。toast 走 naive 全局 $message，缺失时 console.warn。 */
export function toBridgeError(code: BridgeErrorCode, message: string): BridgeError {
  return { code, message: message || code };
}

export function reportBridgeError(err: BridgeError, retry?: () => void): void {
  try {
    console.warn(`[bridge] ${err.code}: ${err.message}`);
  } catch {
    /* console 不可用也不阻断 */
  }
  try {
    const w = window as unknown as {
      $message?: { error: (s: string) => void; warning: (s: string) => void };
      $notification?: { error: (o: { title: string; content: string }) => void };
    };
    if (retry && w.$notification) {
      w.$notification.error({ title: `桥接失败 [${err.code}]`, content: `${err.message}（可重试）` });
    } else {
      w.$message?.error?.(`[${err.code}] ${err.message}`);
    }
  } catch (e) {
    console.warn('[bridge] toast failed:', e);
  }
  void retry;
}

/** Date 序列化：CefSharp 把 C# DateTime 转成 JS Date，旧实现 Object.entries(Date) → {}。
 * 本函数深递归把 Date 还原为 ISO 字符串，保证往返契约始终是 string（proxy 唯一入口）。 */
export function serializeWithDates<T>(input: T): T {
  if (input instanceof Date) {
    try {
      return (Number.isNaN(input.getTime()) ? new Date().toISOString() : input.toISOString()) as unknown as T;
    } catch (e) {
      console.warn('[bridge] date serialize failed:', e);
      return new Date().toISOString() as unknown as T;
    }
  }
  if (input === null || input === undefined || typeof input !== 'object') return input;
  if (Array.isArray(input)) return (input as unknown[]).map((i) => serializeWithDates(i)) as unknown as T;
  const out: Record<string, unknown> = {};
  for (const [k, v] of Object.entries(input as Record<string, unknown>)) {
    out[k] = serializeWithDates(v);
  }
  return out as unknown as T;
}

export function isBridgeError(v: unknown): v is BridgeError {
  if (!v || typeof v !== 'object') return false;
  const o = v as Record<string, unknown>;
  return typeof o['code'] === 'string' && typeof o['message'] === 'string';
}

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
  supportsVision?: boolean;
  maxImagesPerTurn?: number;
  visionDetail?: string | null;
  includeReasoningInHistory?: boolean;
}

// need09-05：聊天图片引用（缺字段/null/空 = 纯文本）
export interface ChatImageRef {
  imageId: string;
  fileName: string;
  mime: string;
  relPath: string;
  width: number;
  height: number;
  sizeBytes: number;
  sha16: string;
  lost?: boolean;
}

// need09-03：模型多模态能力位归一化（缺省 false/4/high；maxImagesPerTurn 0=默认4；显式 auto 保留，缺失/非法回 high）
export function normalizeVisionFlag(m: Record<string, unknown>): void {
  const r = m as unknown as {
    supportsVision?: unknown;
    maxImagesPerTurn?: unknown;
    visionDetail?: unknown;
    includeReasoningInHistory?: unknown;
  };
  if (typeof r.supportsVision !== 'boolean') r.supportsVision = false;
  if (!Number.isFinite(r.maxImagesPerTurn as number)) r.maxImagesPerTurn = 4;
  r.maxImagesPerTurn = Math.trunc(r.maxImagesPerTurn as number);
  if (r.maxImagesPerTurn === 0) r.maxImagesPerTurn = 4;
  if (r.visionDetail == null || r.visionDetail === '') r.visionDetail = 'high';
  if (!['auto', 'low', 'high'].includes(String(r.visionDetail))) r.visionDetail = 'high';
  // 历史思考链默认随上下文发出（老 providers.json 缺字段回退 true）
  if (typeof r.includeReasoningInHistory !== 'boolean') r.includeReasoningInHistory = true;
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
  // need13：父子寄宿（老数据缺字段即 main）
  parentSessionId?: string | null;
  agentRole?: string;
  subLabel?: string | null;
  subStatus?: string | null;
  createdByCallId?: string | null;
  taskPrompt?: string | null;
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
  images?: ChatImageRef[] | null;
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
  version: number;
  enableHaloPrefix: boolean;
  enableIdentity: boolean;
  enableRuntime: boolean;
  enableCmdPython: boolean;
  enableToolGuide: boolean;
  enableEphemeral: boolean;
  enableCustom: boolean;
  customPlacement: 'head' | 'tail';
  customText: string;
  identityText: string;
  runtimeTemplate: string;
  cmdPythonText: string;
  toolGuideTemplate: string;
  ephemeralTemplate: string;
  scope: string;
  updatedAtUtc: string;
  isDefault?: boolean;
  fallbackReason?: string | null;
}
// need08：智能体行为配置（config/agent.json；超时毫秒，0=无限）
// need13：+ 子代理 7 字段（缺字段=老文件，读回默认）
// need17：+ preset/allowlist/denylist/strict/turnTimeout（null/空=默认Coding；只用preset+加减，后端ToolPreset校验）
export interface AgentConfigDocument {
  version: number;
  execDefaultTimeoutMs: number;
  execMaxTimeoutMs: number;
  maxIterations: number;
  turnTimeoutMs?: number;
  subBarrierBufferMs?: number;
  subAgentEnabled: boolean;
  subMaxParallel: number;
  subMaxIterations: number;
  subAllowExecCommand: boolean;
  subAllowFileWrite: boolean;
  subAllowSkillWrite: boolean;
  subAllowMemoryWrite: boolean;
  subAllowAskUser: boolean;
  strictToolsMode?: number | string;
  toolsPreset?: string | null;
  toolsAllowlist?: string[] | null;
  toolsDenylist?: string[] | null;
  allowedCommands?: string[] | null;
  scope: string;
  updatedAtUtc: string;
  isDefault?: boolean;
  fallbackReason?: string | null;
}
// need13：子代理运行时 DTO（与后端 SubRuntimeInfo 同构）
export interface SubRuntimeItem {
  subId: string;
  label: string;
  status: string;
  iteration: number;
  tool: string;
  elapsedMs: number;
}
export interface SubPolicyDto {
  enabled: boolean;
  maxParallel: number;
  maxIterations: number;
  exec: boolean;
  file: boolean;
  skill: boolean;
  memory: boolean;
}
export interface SubRejectionInfo {
  code: string;
  atUtc: string;
  callId: string;
}
export interface SubRuntimeInfo {
  subs: SubRuntimeItem[];
  policy: SubPolicyDto;
  lastRejection?: SubRejectionInfo | null;
  recentRejections?: SubRejectionInfo[];
}
export interface GoalMilestoneDto {
  id: string;
  title: string;
  done: boolean;
}
export interface SessionGoalDto {
  title: string;
  description: string;
  milestones: GoalMilestoneDto[];
  percent: number;
  note: string;
  updatedAtUtc: string;
}
export interface SubSessionDetailDto {
  subId: string;
  parentSessionId: string;
  label: string;
  status: string;
  taskPrompt?: string | null;
  createdByCallId?: string | null;
  messageCount: number;
  iterations: number;
  toolExecutions: number;
  inputTokens: number;
  outputTokens: number;
  cachedTokens: number;
  billedPrompt: number;
  billedCompletion: number;
  errorCode?: string | null;
  errorMessage?: string | null;
  createdAtUtc: string;
  updatedAtUtc: string;
}
export interface PromptBlockPreview {
  blockId: string;
  enabled: boolean;
  renderedText: string;
  estimatedTokens: number;
  isStable: boolean;
}
export interface PromptPreviewDto {
  assembledText: string;
  blocks: PromptBlockPreview[];
  totalEstimatedTokens: number;
  stableEstimatedTokens: number;
  prefixHash: string;
  workspaceId: string;
  unknownVariables: string[];
  isDefault: boolean;
  fallbackReason?: string | null;
}
export interface LastSentPromptSnapshot {
  reqId: string;
  sessionId: string;
  workspaceId: string;
  prefixHash: string;
  charLength: number;
  sentAtUtc: string;
}
export interface PromptDiagnosticsDto {
  currentPreview: PromptPreviewDto;
  lastSent?: LastSentPromptSnapshot | null;
  previewEqualsLastSent: boolean;
  compressorPromptPreview: string;
  titlePromptPreview: string;
}
// need07：历史右键（截断/分支）
export interface HistoryTruncateResultDto {
  success: boolean;
  errorCode?: string | null;
  keptMessages: number;
  removedMessages: number;
  replayText?: string | null;
  backupFileName?: string | null;
}
export type SkillMemoryKind = 'skill' | 'memory';
export type SkillMemoryScope = 'global' | 'project';
export interface HistoryHealResultDto {
  success: boolean;
  errorCode?: string | null;
  syntheticAppended: number;
  orphanToolsRemoved: number;
  keptMessages: number;
  backupFileName?: string | null;
}
export interface HistoryDeleteResultDto {
  success: boolean;
  errorCode?: string | null;
  keptMessages: number;
  syntheticAppended: number;
  orphanToolsRemoved: number;
  backupFileName?: string | null;
}
export type SkillMemoryScopeFilter = 'global' | 'project' | 'all';
export interface SkillMemoryListItem {
  name: string;
  scope: SkillMemoryScope;
  title: string;
  summary: string;
  pin: string;
  enabled: boolean;
  priority: number;
  version: number;
  estTokens: number;
  chars: number;
  hash: string;
  hasAssets: boolean;
  updatedAtUtc: string;
  dirPath: string;
}
export interface SkillMemoryDetail extends SkillMemoryListItem {
  bodyPreview: string;
  truncated: boolean;
  bodyTotalChars: number;
}
export interface SkillMemoryOpResult {
  ok: boolean;
  bakFileName?: string | null;
  notice: string;
}
// need12：用量分析 DTO（与后端 UsageMetricsService 同构；preset 由后端换算，前端只透传）
export type UsagePreset = 'today' | 'yesterday' | 'last3' | 'last7' | 'last30' | 'thisWeek' | 'thisMonth' | 'all' | 'custom';
export interface UsageFilter {
  preset: UsagePreset;
  startDate?: string;
  endDate?: string;
  providerIds?: string[];
  modelKeys?: string[];
  sources?: string[];
  estimatedOnly?: boolean;
}
export interface UsageProviderOption {
  id: string;
  name: string;
}
export interface UsageModelOption {
  providerId: string;
  providerName: string;
  modelId: string;
  display: string;
  key: string;
  label: string;
}
export interface UsageSourceOption {
  id: string;
  name: string;
}
export interface UsageFilterOptions {
  providers: UsageProviderOption[];
  models: UsageModelOption[];
  sources?: UsageSourceOption[];
}
export interface UsageOverview {
  totalRequests: number;
  sumInput: number;
  sumOutput: number;
  sumCached: number;
  hitRate: number;
  activeModels: number;
}
export interface UsageTrendSeries {
  key: string;
  providerName: string;
  modelId: string;
  values: number[];
}
export interface UsageTrend {
  dates: string[];
  perModel: UsageTrendSeries[];
  hitRates: number[];
  totals: number[];
}
export interface UsageProviderShareItem {
  providerId: string;
  name: string;
  value: number;
  share: number;
}
export interface UsageRowDto {
  id: number;
  requestDate: string;
  requestTime: string;
  requestDatetime: string;
  providerName: string;
  modelId: string;
  modelDisplay: string;
  protocol: string;
  inputTokens: number;
  outputTokens: number;
  cachedTokens: number;
  hitRate: number;
  status: string;
  source: string;
  estimated: number;
}
export interface UsagePage {
  total: number;
  page: number;
  pageSize: number;
  rows: UsageRowDto[];
}
export interface UsageDbStatus {
  dbPath: string;
  recordCount: number;
  dbSizeBytes: number;
  oldestDate?: string | null;
  newestDate?: string | null;
}
// UI活跃状态（后端 config/ui-state.json，只恢复到main；单实例互斥）
export interface UiActiveState {
  workspaceId: string | null;
  sessionId: string | null;
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
  /** 聊天 markdown 链接统一出口：后端转系统浏览器 / explorer，永不在 CEF 内渲染。 */
  openExternalLink: (href: string, workspaceRoot?: string) => Promise<BridgeResponse<boolean>>;
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
    modelId?: string,
    attachmentsJson?: string,
    requestId?: string
  ) => Promise<BridgeResponse<string>>;
  // need09-05：缩略图（只传缩略图不传原图，长边≤256px dataURL）
  getImageThumbnail: (workspaceId: string, sessionId: string, imageId: string) => Promise<BridgeResponse<string>>;
  // need09-06：问询回答提交（ticket 错返回 ASK_TICKET_MISMATCH）
  resolveAskUser: (sessionId: string, ticketId: string, answersJson: string) => Promise<BridgeResponse<boolean>>;

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
  getContextBudget: (
    providerId: string,
    modelId: string,
    workspaceId: string,
    sessionId: string
  ) => Promise<BridgeResponse<ContextBudgetStatusDto>>;
  // need05-03: /compact 上下文压缩
  compactSession: (
    workspaceId: string,
    sessionId: string,
    providerId?: string,
    modelId?: string,
    keepLastGroups?: number
  ) => Promise<BridgeResponse<CompactionResultDto>>;
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
  // need13：子代理桥接
  getSubSessions: (ws: string, parent: string) => Promise<BridgeResponse<SessionMetadata[]>>;
  cancelSubAgent: (ws: string, sub: string) => Promise<BridgeResponse<boolean>>;
  cancelAgentTree: (main: string) => Promise<BridgeResponse<boolean>>;
  // need22-P2：cancelAgentTurn 为整树取消别名（后端 CancelAgentTurn 即调 CancelAgentTree；老调用保留）
  cancelAgentTurn: (sessionId: string) => Promise<BridgeResponse<boolean>>;
  getSubRuntime: (ws: string, parent: string) => Promise<BridgeResponse<SubRuntimeInfo>>;
  // need16：Goal + 子会话详情
  getGoal: (ws: string, sess: string) => Promise<BridgeResponse<SessionGoalDto>>;
  saveGoal: (ws: string, sess: string, goalJson: string) => Promise<BridgeResponse<SessionGoalDto>>;
  clearGoal: (ws: string, sess: string) => Promise<BridgeResponse<boolean>>;
  getSubSessionDetail: (ws: string, sub: string) => Promise<BridgeResponse<SubSessionDetailDto>>;
  getSubSessionMessages: (
    ws: string,
    sub: string,
    limit?: number,
    beforeMessageId?: string
  ) => Promise<BridgeResponse<UnifiedChatMessage[]>>;
  getPromptDiagnostics: (workspaceId: string, sessionId?: string) => Promise<BridgeResponse<PromptDiagnosticsDto>>;
  // need07
  truncateSessionHistory: (
    workspaceId: string,
    sessionId: string,
    anchorMessageId: string,
    removeAnchor: boolean
  ) => Promise<BridgeResponse<HistoryTruncateResultDto>>;
  healSessionHistory: (workspaceId: string, sessionId: string) => Promise<BridgeResponse<HistoryHealResultDto>>;
  deleteSessionMessage: (workspaceId: string, sessionId: string, messageId: string) => Promise<BridgeResponse<HistoryDeleteResultDto>>;
  // need10：技能与记忆只读管理（查/开关/删除；无新建与编辑）
  listSkillMemory: (
    kind: SkillMemoryKind,
    scope: SkillMemoryScopeFilter,
    workspaceId?: string
  ) => Promise<BridgeResponse<SkillMemoryListItem[]>>;
  getSkillMemoryDetail: (
    kind: SkillMemoryKind,
    scope: SkillMemoryScope,
    name: string,
    workspaceId?: string
  ) => Promise<BridgeResponse<SkillMemoryDetail>>;
  setSkillMemoryEnabled: (
    kind: SkillMemoryKind,
    scope: SkillMemoryScope,
    name: string,
    enabled: boolean,
    workspaceId?: string
  ) => Promise<BridgeResponse<SkillMemoryOpResult>>;
  deleteSkillMemory: (
    kind: SkillMemoryKind,
    scope: SkillMemoryScope,
    name: string,
    workspaceId?: string
  ) => Promise<BridgeResponse<SkillMemoryOpResult>>;
  openSkillMemoryFolder: (
    kind: SkillMemoryKind,
    scope: SkillMemoryScope,
    name: string,
    workspaceId?: string
  ) => Promise<BridgeResponse<boolean>>;
  branchSession: (
    workspaceId: string,
    sourceSessionId: string,
    anchorMessageId: string,
    includeAnchor: boolean
  ) => Promise<BridgeResponse<SessionMetadata>>;
  // need12：用量分析（服务端聚合只读）
  getUsageFilterOptions: () => Promise<BridgeResponse<UsageFilterOptions>>;
  getUsageOverview: (filter: UsageFilter | string) => Promise<BridgeResponse<UsageOverview>>;
  getUsageTrend: (filter: UsageFilter | string, metric?: string) => Promise<BridgeResponse<UsageTrend>>;
  getUsageProviderShare: (filter: UsageFilter | string) => Promise<BridgeResponse<UsageProviderShareItem[]>>;
  queryUsagePage: (filter: UsageFilter | string, page: number, pageSize: number) => Promise<BridgeResponse<UsagePage>>;
  getUsageDbStatus: () => Promise<BridgeResponse<UsageDbStatus>>;
  // UI活跃状态（后端config/ui-state.json，只恢复到main）
  getLastActive: () => Promise<BridgeResponse<UiActiveState>>;
  setLastActive: (workspaceId: string, sessionId: string) => Promise<BridgeResponse<boolean>>;
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

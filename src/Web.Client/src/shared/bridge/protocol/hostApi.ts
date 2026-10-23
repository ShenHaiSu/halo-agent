/** need21-F02: HostBridgeApi 接口（旧 src/bridge/types.ts 尾部原样迁移，签名不变）。 */
import type { BridgeResponse, StorageConfigInfo, StorageMigrationResult, StorageValidationResult, SystemRuntimeInfo } from './base';
import type {
  GlobalNetworkSettings,
  ProviderConfig,
  ProviderEndpointPreviewDto,
  ProvidersConfigDocument,
  ProxyConnectivityTestResult,
  SystemProxySnapshot,
} from './providers';
import type { UnifiedLlmRequestDto } from './llm';
import type {
  AgentConfigDocument,
  CompactionResultDto,
  ContextBudgetStatusDto,
  HistoryDeleteResultDto,
  HistoryHealResultDto,
  HistoryTruncateResultDto,
  PromptConfigDocument,
  PromptDiagnosticsDto,
  PromptPreviewDto,
  SessionGoalDto,
  SessionMetadata,
  SkillMemoryDetail,
  SkillMemoryKind,
  SkillMemoryListItem,
  SkillMemoryOpResult,
  SkillMemoryScope,
  SkillMemoryScopeFilter,
  SubRuntimeInfo,
  SubSessionDetailDto,
  UiActiveState,
  UnifiedChatMessage,
  UsageDbStatus,
  UsageFilter,
  UsageFilterOptions,
  UsageOverview,
  UsagePage,
  UsageProviderShareItem,
  UsageTrend,
  WorkspaceInfo,
} from './domain';

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

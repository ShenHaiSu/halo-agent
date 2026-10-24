/** need21-F02: 桥协议类型（单源：旧 src/bridge/types.ts 按域拆分，行为不变）。 */
import type { ChatImageRef } from './providers';

export interface WorkspaceInfo {
  id: string;
  name: string;
  /** 用户自定义别名（纯前端视觉；null/空 = 显示 name）。不进提示词/请求头。 */
  alias?: string | null;
  /** 展示名（= alias || name）；后端 computed 带出，前端可本地兜底。 */
  displayName?: string;
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
// 单轮最大时间 turnTimeoutMs：0=无限时间，有限值 1000..600000（10min/600s），设置与诊断中心·智能体行为可配
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
  /** 已完成轮次（后端真实统计） */
  iteration: number;
  /** 工具调用总次数（后端真实统计） */
  toolCount: number;
  /** 最近一次工具名（后端真实统计，无则空串） */
  lastTool: string;
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

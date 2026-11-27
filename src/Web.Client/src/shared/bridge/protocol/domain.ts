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
  historyVersion?: number;
}

export type TimelineBlockKind = 'reasoning' | 'text' | 'tool_call' | 'tool_result';
export type TimelineBlockTag = 'process' | 'final' | 'pending';
export type TimelineTurnStatus = 'completed' | 'truncated' | 'cancelled' | 'error';

export interface TimelineBlock {
  blockId: string;
  turnId: string;
  iteration: number;
  seq: number;
  kind: TimelineBlockKind;
  tag: TimelineBlockTag;
  callId?: string | null;
  charCount: number;
  preview: string;
  content: string;
  contentRef?: string | null;
  truncated: boolean;
}

export interface TurnSummary {
  turnId: string;
  turnSeq: number;
  iterEnd: number;
  userPreview: string;
  userContent?: string;
  finalBlockId?: string | null;
  finalPreview: string;
  blocks: number;
  tools: number;
  failed: number;
  durationMs: number;
  chars: number;
  status: TimelineTurnStatus;
  lastRequestId: string;
  createdAtUtc: string;
  updatedAtUtc: string;
  anchorKey: string;
  /** P007：轮次语义；'turn' = 普通轮，'compact' = 压缩分隔（缺省视为 'turn'，见 10-§6）。 */
  kind?: 'turn' | 'compact' | string;
  /** P007：本次压缩覆盖的消息条数（仅 compact 轮）。 */
  removedMessages?: number;
  /** 后端投影的真实用户消息主键；只用于历史操作，不参与导航渲染。 */
  userMessageId?: string | null;
  /** 后端投影的本轮末条助手消息主键；失败轮或压缩轮为空。 */
  assistantMessageId?: string | null;
}

/** 重放图片引用：lost 的图片只产生警告，不参与重新发送。 */
export interface HistoryReplayImageDto {
  imageId: string;
  fileName: string;
  mime: string;
  width: number;
  height: number;
  sizeBytes: number;
  sha16: string;
  lost: boolean;
}

/** 历史能力位：探测失败、字段缺失或旧后端方法不存在均归一为全部不支持。 */
export interface HistoryCapabilities {
  historyAnchors: boolean;
  replayImages: boolean;
  atomicHistoryMutex: boolean;
  strictBranchRollback: boolean;
}

export const UnsupportedHistoryCapabilities: HistoryCapabilities = {
  historyAnchors: false,
  replayImages: false,
  atomicHistoryMutex: false,
  strictBranchRollback: false,
};

/** 能力归一：字段缺失、类型错误或探测失败一律视为不支持，禁止伪造支持。 */
export function normalizeHistoryCapabilities(raw: unknown): HistoryCapabilities {
  if (!raw || typeof raw !== 'object') return { ...UnsupportedHistoryCapabilities };
  const source = raw as Record<string, unknown>;
  const flag = (key: keyof HistoryCapabilities): boolean => (typeof source[key] === 'boolean' ? (source[key] as boolean) : false);
  return {
    historyAnchors: flag('historyAnchors'),
    replayImages: flag('replayImages'),
    atomicHistoryMutex: flag('atomicHistoryMutex'),
    strictBranchRollback: flag('strictBranchRollback'),
  };
}

/** 成功结果内的警告，IMAGE_MISSING 不作为 Bridge 失败码。 */
export interface HistoryOperationWarning {
  code: 'IMAGE_MISSING';
  message: string;
}

export interface HistoryOperationResult {
  ok: boolean;
  code?: string;
  message?: string;
  warnings?: HistoryOperationWarning[];
}

export interface HistoryReplayResult extends HistoryOperationResult {
  replayText: string;
  replayImages: HistoryReplayImageDto[];
  destructiveCommitCompleted: boolean;
  recoveryToken?: string;
}

export interface HistoryBranchResult extends HistoryOperationResult {
  session?: unknown;
  newSessionId?: string;
  failedStage?: string;
  switched?: boolean;
}
export interface TimelineBlockContent {
  blockId: string;
  callId?: string | null;
  functionName?: string | null;
  seq?: number;
  kind: TimelineBlockKind;
  tag: TimelineBlockTag;
  markdown: string;
  preview: string;
  truncated: boolean;
  /** 逐工具成败位：tool_result 行透出；null/undefined=未知（老数据走回退链），与 false 严格区分。 */
  success?: boolean | null;
  argsJson?: string | null;
  resultText?: string | null;
  resultRef?: string | null;
}

export interface TimelineBlockContentsResponse {
  items: Record<string, TimelineBlockContent>;
  missing: string[];
}

export interface TimelineToolResult {
  argsJson: string;
  resultText: string;
  resultRef?: string | null;
  functionName?: string | null;
  /** 逐工具成败位：null/undefined=未知（老数据走回退链），与 false 严格区分。 */
  success?: boolean | null;
}

export type TimelineTagReason = 'demote' | 'finalize' | 'flip';

export interface TimelineTagUpdated {
  blockId: string;
  tag: TimelineBlockTag;
  reason: TimelineTagReason;
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
  /** P007：'compact_stub' = 压缩桩（其余为 undefined，见 10-§6）。 */
  kind?: string | null;
  /** P007：桩元数据 JSON 原文（前端仅在需要 removedCount 时解析）。 */
  metaJson?: string | null;
}
/** need03-03-B：标题链路首轮取数（后端 GetFirstRoundTexts 直读存储，v2 会话可用）。 */
export interface FirstRoundTexts {
  firstUser: string;
  assistant: string;
  userCount: number;
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
  /** P007：占用基线来源 exact | compact-estimate | estimated（缺省 exact，见 10-§6）。 */
  occupancyBaseline?: string;
  occupancyState?: string; // known | unknown | estimated
  billedPromptTokens?: number; // Σ账单 prompt
  billedCompletionTokens?: number;
  // need05-06：本地预测（发送前分块 LCP 推测；实测优先，预测只做未知态兜底）
  predictedCacheHitRate?: number | null;
  predictedCachedTokens?: number | null;
  prefixDivergenceBlock?: string | null;
}

export type CompactionOutcome = 'completed' | 'noop' | 'failed';

export interface CompactionResultDto {
  success: boolean;
  outcome?: CompactionOutcome;
  errorCode?: string | null;
  errorMessage?: string | null;
  noOpReason?: string | null;
  removedMessages: number;
  keptMessages: number;
  summaryPreview: string;
  backupFileName?: string | null;
  stubMessageId?: string | null;
  stubTurnId?: string | null;
  summaryOutputTokens?: number;
  compactLlmCallCount?: number | null;
  metadataWarning?: string | null;
}
/** 压缩完成事件增量载荷，字段只增不改。 */
export interface CompactCompletedPayload {
  removedMessages?: number;
  keptMessages?: number;
  summaryPreview?: string | null;
  backupFileName?: string | null;
  stubMessageId?: string | null;
  stubTurnId?: string | null;
  summaryTokens?: number;
  compactLlmCallCount?: number | null;
  occupancyBaseline?: string;
  metadataWarning?: string | null;
  updatedAtUtc?: string | null;
}
export interface CompactNoOpPayload {
  noOpReason?: string | null;
  updatedAtUtc?: string | null;
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
/** 历史右键截断结果（分支与重放共享 DTO 基线）。 */
export interface HistoryTruncateResultDto {
  success: boolean;
  errorCode?: string | null;
  keptMessages: number;
  removedMessages: number;
  replayText?: string | null;
  replayImages?: HistoryReplayImageDto[] | null;
  backupFileName?: string | null;
  destructiveCommitCompleted: boolean;
  recoveryToken?: string | null;
}
export type SessionDeletionOutcome = 'deleted' | 'partial' | 'failed' | 'notFound';
export type SessionDeletionStage = 'notRequested' | 'completed' | 'failed';

export interface SessionDeletionResultDto {
  outcome: SessionDeletionOutcome;
  errorCode?: string | null;
  destructiveCommitCompleted: boolean;
  backupFileName?: string | null;
  deletedSessionIds: string[];
  residualSessionIds: string[];
  database: SessionDeletionStage;
  directories: SessionDeletionStage;
  index: SessionDeletionStage;
  warnings: string[];
}

export interface DeleteSessionCommandResult {
  outcome: SessionDeletionOutcome;
  destructiveCommitCompleted: boolean;
  deletedSessionIds: string[];
  residualSessionIds: string[];
  backupFileName?: string | null;
  errorCode?: string | null;
  message?: string;
  warnings: string[];
}

/** 工作区移除模式：解绑保留物理数据，彻底删除只清两个明确根目录。 */
export type WorkspaceRemovalMode = 'unbind' | 'purge';
export type WorkspaceRemovalOutcome = 'completed' | 'completedWithWarnings' | 'failed' | 'notFound';
export type WorkspaceResourceStatus = 'notRequested' | 'deleted' | 'retained' | 'failed';

export interface WorkspaceRemovalResultDto {
  outcome: WorkspaceRemovalOutcome;
  mode: WorkspaceRemovalMode;
  errorCode?: string | null;
  workspaceUnbound: boolean;
  sessionData: WorkspaceResourceStatus;
  haloAgent: WorkspaceResourceStatus;
  haloAgentReason?: string | null;
  failedPaths: string[];
  warnings: string[];
}

/** Store 层命令结果：与 DTO 同构，前端只按 workspaceUnbound 决定是否移除本地项。 */
export interface WorkspaceRemovalCommandResult {
  outcome: WorkspaceRemovalOutcome;
  mode: WorkspaceRemovalMode;
  workspaceUnbound: boolean;
  sessionData: WorkspaceResourceStatus;
  haloAgent: WorkspaceResourceStatus;
  haloAgentReason?: string | null;
  failedPaths: string[];
  warnings: string[];
  errorCode?: string | null;
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
// 外观设置真相源（后端 config/appearance.json）。字段与 shared/ui/appearance.ts 的
// AppearanceSettings 严格对齐；dev(http://localhost:5173)/release(app://local) 的
// localStorage 按 origin 隔离，故不再由浏览器 localStorage 充当真相源。
export interface AppearanceSettingsDto {
  themeMode: 'dark' | 'light';
  themeColor: string;
  fontSize: number;
  chineseFont: string;
  englishFont: string;
  monoFont: string;
  chatWidthMode: 'centered' | 'full';
  sendKeyMode: 'enter' | 'ctrl-enter';
}

// [注意] 本文件由 scripts/gen-contract-types.mjs 自动生成，请勿手改。
// 来源: contract/agent.schema.json（协议版本 4）

/** PermissionMode */
export type PermissionMode = "ask" | "acceptEdits" | "bypassPermissions" | "yolo"

/** AskKind */
export type AskKind = "command" | "write" | "rounds"

/** MessageRole */
export type MessageRole = "user" | "assistant" | "tool" | "system"

/** ToolCallView */
export interface ToolCallView {
  id: string
  name: string
  args?: string
  ok?: boolean | null
  output?: string | null
  kind?: "function" | "unknown"
  rawType?: string | null
  payload?: Record<string, unknown> | null
  argsTotal?: number
  outputTotal?: number
  truncated?: boolean
}

/** MessageView */
export interface MessageView {
  id: string
  role: MessageRole
  text?: string
  reasoning?: string | null
  toolCalls?: ToolCallView[]
  injected?: boolean
  tsMs?: number
  hasImages?: boolean
  attachmentCount?: number
  reasoningKind?: "full" | "summary"
  responseId?: string | null
  ord?: number
}

/** SessionSummary */
export interface SessionSummary {
  id: string
  title: string
  note?: string | null
  cwd?: string | null
  headless?: boolean
  mtimeMs: number
  userMsgs?: number
  assistantMsgs?: number
}

/** SkillSummary */
export interface SkillSummary {
  name: string
  description: string
  source: "builtin" | "user" | "project"
  disableModelInvocation?: boolean
  hasEntry?: boolean
}

/** MemorySummary */
export interface MemorySummary {
  filename: string
  name: string
  description?: string
  mtype?: string
  scope: "global" | "project"
  updatedMs?: number
}

/** ToolInfo */
export interface ToolInfo {
  name: string
  description: string
  source?: "builtin" | "mcp" | "skill"
}

/** SnapshotInfo */
export interface SnapshotInfo {
  index: number
  session: string
  orig: string
  action: string
  tsMs: number
  external?: boolean
}

/** Note */
export interface Note {
  level: "info" | "warn" | "error"
  text: string
}

/** PendingAsk */
export interface PendingAsk {
  askId: string
  kind: AskKind
  title: string
  body: string
  warning?: string | null
  allowAlways?: boolean
  used?: number | null
}

/** HeaderPair */
export interface HeaderPair {
  name: string
  value: string
}

/** StateSnapshot */
export interface StateSnapshot {
  protocol: number
  engine: {
  version?: string
  pid?: number
  startedAtMs?: number
  osVersion?: string
  isWin7?: boolean
}
  session: {
  id?: string
  cwd?: string
  historyPath?: string | null
}
  agent: {
  busy?: boolean
  turnId?: string | null
  mode?: PermissionMode
  modeLabel?: string
  persona?: string
  model?: string
  provider?: string
  baseUrl?: string
  apiKeySet?: boolean
  contextWindow?: number | null
  maxTurns?: number
  retry?: number
  configured?: boolean
  round?: {
  used?: number
  limit?: number
}
  providerDisplay?: string
  modelDisplay?: string
  protocol?: "chat" | "responses"
  maxTokens?: number | null
  acceptsImages?: boolean
  includeReasoning?: boolean
  proxyInUse?: boolean
  proxyKind?: "None" | "Manual" | "System"
  proxySource?: string
  proxyMaskedUrl?: string | null
}
  usage: {
  inputTotal?: number
  outputTotal?: number
  lastPromptTokens?: number | null
  lastCachedTokens?: number | null
  estimatedInitialTokens?: number | null
  sessionLlmCalls?: number
}
  queue: string[]
  pendingAsk?: PendingAsk | null
  notes?: Note[]
  update?: {
  available?: boolean
  latestVersion?: string | null
}
  turns: TurnView[]
}

/** 主题摘要（只含元信息，不含 tokens） */
export interface ThemeSummary {
  id: string
  name: string
  basedOn?: string | null
  source: "builtin" | "user"
}

/** 主题文件（完整 token 集） */
export interface ThemeFile {
  id: string
  name: string
  basedOn?: string | null
  tokens: Record<string, unknown>
}

/** 上下文压缩分隔栏视图数据 */
export interface CompactDividerView {
  seq: number
  summary: string
  removedTurns: number
  keptTurns: number
  tsMs: number
}

/** TurnView */
export interface TurnView {
  turnId: string
  user: MessageView
  injected?: MessageView[]
  process?: MessageView[]
  final: MessageView
  dividerBefore?: CompactDividerView
}

/** 用量明细行 */
export interface UsageRow {
  tsMs: number
  sessionId: string
  turnId: string
  providerId: string
  providerDisplay?: string
  modelId: string
  modelDisplay?: string
  protocol?: string
  prompt: number
  cached: number
  output: number
  total: number
  hitRate: number
  llmCalls?: number
  truncated?: number
}

/** 按天聚合 */
export interface UsageByDay {
  day: string
  input: number
  cached: number
  output: number
}

/** 按双键聚合 */
export interface UsageByModel {
  providerId: string
  providerDisplay?: string
  modelId: string
  modelDisplay?: string
  input: number
  cached: number
  output: number
  requests: number
  llmCalls?: number
  hitRate: number
}

/** 帧信封：request */
export interface RequestFrame {
  t: "req"
  id: number
  m: string
  p?: Record<string, unknown> | null
}

/** 帧信封：response */
export interface ResponseFrame {
  t: "res"
  id: number
  ok: boolean
  r?: Record<string, unknown> | unknown[] | string | number | boolean | null
  e?: {
  code: string
  msg: string
  hint?: string | null
} | null
}

/** 帧信封：event */
export interface EventFrame {
  t: "evt"
  seq: number
  k: string
  p?: Record<string, unknown> | null
}

/** 出站帧：要么是事件，要么是应答（统一走一个 FIFO，保证顺序）。 */
export type HaloFrame = RequestFrame | ResponseFrame | EventFrame

/** 方法表：params 为 null 的方法映射为 void（调用时可省略）。 */
export interface HaloMethods {
  /** system.ping */
  'system.ping': { params: void; result: {
  pong: boolean
  version: string
  protocol: number
  pid: number
  uptimeMs?: number
  serverTimeMs?: number
} }
  /** system.info */
  'system.info': { params: void; result: {
  version?: string
  protocol?: number
  pid?: number
  osVersion?: string
  isWin7?: boolean
  is64BitOs?: boolean
  is64BitProcess?: boolean
  runtime?: string
  dataDir?: string
  startedAtMs?: number
} }
  /** system.protocol */
  'system.protocol': { params: void; result: {
  version?: number
  encoding?: string
  maxFrameBytes?: number
  methods?: string[]
  events?: string[]
} }
  /** system.shutdown */
  'system.shutdown': { params: {
  graceful?: boolean
}; result: {
  ok?: boolean
} }
  /** system.diagnostics */
  'system.diagnostics': { params: void; result: {
  compact?: {
  lastRemoved?: number
  lastBefore?: number
  lastAfter?: number
  lastTsMs?: number
}
} }
  /** agent.prompt */
  'agent.prompt': { params: {
  text: string
}; result: {
  accepted: boolean
  turnId: string
} }
  /** agent.queue */
  'agent.queue': { params: {
  text: string
}; result: {
  queued?: boolean
  position?: number
} }
  /** agent.unqueue */
  'agent.unqueue': { params: {
  text: string
}; result: {
  removed?: boolean
} }
  /** agent.cancel */
  'agent.cancel': { params: {
  turnId?: string | null
}; result: {
  cancelled?: boolean
} }
  /** agent.answer */
  'agent.answer': { params: {
  askId: string
  allow?: boolean
  always?: boolean
}; result: {
  accepted?: boolean
  reason?: string | null
} }
  /** agent.drain */
  'agent.drain': { params: void; result: {
  text?: string | null
} }
  /** agent.resync */
  'agent.resync': { params: void; result: StateSnapshot }
  /** session.new */
  'session.new': { params: void; result: {
  id?: string
} }
  /** session.resume */
  'session.resume': { params: {
  id: string
}; result: {
  count?: number
  turns?: TurnView[]
} }
  /** session.list */
  'session.list': { params: {
  query?: string
}; result: {
  items?: SessionSummary[]
} }
  /** session.rename */
  'session.rename': { params: {
  id: string
  note: string
}; result: {
  ok?: boolean
} }
  /** session.delete */
  'session.delete': { params: {
  id: string
}; result: {
  ok?: boolean
} }
  /** session.clear */
  'session.clear': { params: void; result: {
  ok?: boolean
} }
  /** session.compact */
  'session.compact': { params: void; result: {
  removed?: number
  summary?: string
  totalBefore?: number
  totalAfter?: number
  keptCount?: number
  seq?: number
  removedTurns?: number
  keptTurns?: number
} }
  /** session.setCwd */
  'session.setCwd': { params: {
  path: string
}; result: {
  cwd?: string
} }
  /** state.snapshot */
  'state.snapshot': { params: void; result: StateSnapshot }
  /** state.setMode */
  'state.setMode': { params: {
  mode: PermissionMode
}; result: {
  mode?: PermissionMode
  label?: string
} }
  /** state.setPersona */
  'state.setPersona': { params: {
  name: string
}; result: {
  name?: string
} }
  /** state.setMaxTurns */
  'state.setMaxTurns': { params: {
  value: number
}; result: {
  maxTurns?: number
} }
  /** state.setRetry */
  'state.setRetry': { params: {
  value: number
}; result: {
  retry?: number
} }
  /** settings.get */
  'settings.get': { params: void; result: {
  activeProvider?: string
  activeModel?: string
  migrationRequired?: boolean
  providers?: {
  id?: string
  /** P14 别名：null/空/==id(Ordinal)均视为无别名；有别名时为trim后别名 */
  displayName?: string
  baseUrl?: string
  apiKeySet?: boolean
  headersEnabled?: boolean
  headers?: HeaderPair[]
  useProxy?: boolean
  modelCount?: number
  models?: {
  id?: string
  /** P14 别名：null/空/==id(Ordinal)均视为无别名；有别名时为trim后别名 */
  displayName?: string
  protocol?: "chat" | "responses"
  contextWindow?: number | null
  maxTokens?: number | null
  acceptsImages?: boolean
  includeReasoningHistory?: boolean
}[]
  network?: "follow" | "proxy" | "direct"
  effective?: {
  inUse?: boolean
  source?: string
  maskedUrl?: string | null
  warning?: string | null
}
}[]
  proxy?: {
  mode?: "system" | "direct" | "manual"
  url?: string | null
}
  retry?: number
  maxTurns?: number
  persona?: string
  compactKeepTail?: number
  autoCompactOnLimit?: boolean
  dataDir?: string
  configPath?: string
} }
  /** settings.saveNetwork */
  'settings.saveNetwork': { params: {
  mode?: "system" | "direct" | "manual"
  url?: string | null
  retry?: number
}; result: {
  ok?: boolean
  notes?: string[]
  warnings?: string[]
} }
  /** settings.saveBehavior */
  'settings.saveBehavior': { params: {
  maxTurns?: number
  retry?: number
  persona?: string
  compactKeepTail?: number
  autoCompactOnLimit?: boolean
}; result: {
  ok?: boolean
  warnings?: string[]
} }
  /** settings.probeModels */
  'settings.probeModels': { params: {
  baseUrl?: string
  apiKey?: string | null
  providerId?: string | null
  headers?: HeaderPair[]
  useProxy?: boolean
  clearKey?: boolean
  network?: "follow" | "proxy" | "direct"
}; result: {
  models?: string[]
  via?: {
  source?: string
  maskedUrl?: string | null
}
  latencyMs?: number
} }
  /** settings.mcp.get */
  'settings.mcp.get': { params: void; result: {
  path?: string
  text?: string
} }
  /** settings.mcp.save */
  'settings.mcp.save': { params: {
  text: string
}; result: {
  ok?: boolean
  notes?: string[]
} }
  /** asset.skills.list */
  'asset.skills.list': { params: void; result: {
  items?: SkillSummary[]
  warnings?: string[]
} }
  /** asset.skills.get */
  'asset.skills.get': { params: {
  name: string
}; result: {
  name?: string
  body?: string
  entry?: string | null
  source?: string
} }
  /** asset.skills.save */
  'asset.skills.save': { params: {
  name: string
  body: string
}; result: {
  ok?: boolean
} }
  /** asset.skills.delete */
  'asset.skills.delete': { params: {
  name: string
}; result: {
  ok?: boolean
} }
  /** asset.skills.run */
  'asset.skills.run': { params: {
  name: string
  args?: string
}; result: {
  accepted?: boolean
} }
  /** asset.themes.list */
  'asset.themes.list': { params: void; result: {
  items: ThemeSummary[]
} }
  /** asset.themes.get */
  'asset.themes.get': { params: {
  id: string
}; result: ThemeFile }
  /** asset.personas.list */
  'asset.personas.list': { params: void; result: {
  items?: string[]
} }
  /** asset.personas.get */
  'asset.personas.get': { params: {
  name: string
}; result: {
  name?: string
  body?: string
} }
  /** asset.personas.save */
  'asset.personas.save': { params: {
  name: string
  body: string
}; result: {
  ok?: boolean
} }
  /** asset.personas.delete */
  'asset.personas.delete': { params: {
  name: string
}; result: {
  ok?: boolean
} }
  /** asset.memory.list */
  'asset.memory.list': { params: void; result: {
  items?: MemorySummary[]
} }
  /** asset.memory.get */
  'asset.memory.get': { params: {
  filename: string
  scope: "global" | "project"
}; result: {
  name?: string
  description?: string
  mtype?: string
  scope?: string
  body?: string
} }
  /** asset.memory.save */
  'asset.memory.save': { params: {
  filename?: string | null
  name: string
  description?: string
  mtype?: string
  scope: "global" | "project"
  body: string
}; result: {
  ok?: boolean
  filename?: string
} }
  /** asset.memory.delete */
  'asset.memory.delete': { params: {
  filename: string
  scope: "global" | "project"
}; result: {
  ok?: boolean
} }
  /** tool.list */
  'tool.list': { params: void; result: {
  items?: ToolInfo[]
} }
  /** tool.undoList */
  'tool.undoList': { params: void; result: {
  items?: SnapshotInfo[]
} }
  /** tool.undoRollback */
  'tool.undoRollback': { params: {
  index: number
}; result: {
  ok?: boolean
  orig?: string
} }
  /** tool.mcpStatus */
  'tool.mcpStatus': { params: void; result: {
  servers?: {
  name?: string
  connected?: boolean
  toolCount?: number
  error?: string | null
}[]
} }
  /** fs.listDir */
  'fs.listDir': { params: {
  path: string
}; result: {
  items?: {
  name?: string
  isDir?: boolean
  size?: number
}[]
} }
  /** fs.completePaths */
  'fs.completePaths': { params: {
  prefix: string
}; result: {
  items?: {
  path?: string
  isDir?: boolean
}[]
} }
  /** fs.validatePath */
  'fs.validatePath': { params: {
  path: string
}; result: {
  valid?: boolean
  normalized?: string
  isDir?: boolean
  error?: string | null
} }
  /** update.check */
  'update.check': { params: void; result: {
  available?: boolean
  latestVersion?: string | null
  notes?: string | null
} }
  /** update.install */
  'update.install': { params: void; result: {
  started?: boolean
} }
  /** fs.browse */
  'fs.browse': { params: {
  initialDir?: string | null
  title?: string | null
} | null; result: string | null }
  /** fs.pickFiles */
  'fs.pickFiles': { params: {
  initialDir?: string | null
  filter?: string | null
  multiple?: boolean
} | null; result: string[] | null }
  /** fs.pickSavePath */
  'fs.pickSavePath': { params: {
  initialDir?: string | null
  defaultName?: string | null
  filter?: string | null
} | null; result: string | null }
  /** fs.readImageAsBase64 */
  'fs.readImageAsBase64': { params: {
  path: string
}; result: string | null }
  /** settings.saveProviders */
  'settings.saveProviders': { params: {
  providers?: Record<string, unknown>
  force?: boolean
}; result: {
  ok?: boolean
  notes?: string[]
  warnings?: string[]
  errors?: unknown[]
} }
  /** settings.setActive */
  'settings.setActive': { params: {
  providerId?: string
  modelId?: string
}; result: {
  ok?: boolean
} }
  /** tool.result */
  'tool.result': { params: {
  toolCallId: string
  offset?: number
  limit?: number
}; result: {
  toolCallId: string
  argsSlice?: string
  outputSlice?: string
  argsTotal?: number
  outputTotal?: number
  found: boolean
  reason?: string | null
} }
  /** usage.query */
  'usage.query': { params: {
  fromMs?: number
  toMs?: number
  providerId?: string
  modelId?: string
  sessionId?: string
  tzOffsetMin?: number
  page?: number
  pageSize?: number
}; result: {
  items: UsageRow[]
  total: number
  degraded?: boolean
} }
  /** usage.summary */
  'usage.summary': { params: {
  fromMs?: number
  toMs?: number
  providerId?: string
  modelId?: string
  sessionId?: string
  tzOffsetMin?: number
}; result: {
  input: number
  cached: number
  output: number
  total: number
  requests: number
  llmCalls?: number
  hitRate: number
  byDay: UsageByDay[]
  byModel: UsageByModel[]
  degraded?: boolean
} }
  /** usage.stats */
  'usage.stats': { params: void; result: {
  sizeBytes: number
  rowCount: number
  degraded?: boolean
} }
  /** usage.export */
  'usage.export': { params: {
  fromMs?: number
  toMs?: number
  providerId?: string
  modelId?: string
  sessionId?: string
  tzOffsetMin?: number
}; result: {
  csv: string
  truncated: boolean
  degraded?: boolean
} }
  /** usage.exportFile */
  'usage.exportFile': { params: {
  fromMs?: number
  toMs?: number
  providerId?: string
  modelId?: string
  sessionId?: string
  tzOffsetMin?: number
  /** 绝对路径 .csv（用户经保存对话框选路）；非法/越界报 params.invalid */
  path: string
  /** providerId→假名（前端模糊化映射）；引擎只做 provider 字段级替换 */
  maskMap?: Record<string, unknown>
}; result: {
  path: string
  wrote: number
  truncated: boolean
  degraded?: boolean
} }
  /** usage.clear */
  'usage.clear': { params: {
  beforeMs: number
}; result: {
  removed: number
  degraded?: boolean
} }
}

/** 事件载荷表：kind → payload。ask.* 的 payload 即 PendingAsk。 */
export interface HaloEventPayloads {
  /** turn.started */
  'turn.started': {
  turnId?: string
}
  /** turn.finished */
  'turn.finished': {
  turnId: string
  text: string
  truncated?: boolean
  reason?: string | null
  responseId?: string | null
  finalUsed: number
  /** P24：本次截断是否由用户主动中断造成。ByUser=true 时 truncated 必为 true。 */
  byUser?: boolean
}
  /** text.delta */
  'text.delta': {
  text?: string
  used?: number
}
  /** reasoning.delta */
  'reasoning.delta': {
  text?: string
  kind?: "full" | "summary"
  used?: number
}
  /** tool.started */
  'tool.started': {
  toolCallId?: string
  name?: string
  args?: string
  idleMs?: number | null
  kind?: "function" | "unknown"
  rawType?: string | null
  used?: number
  argsTotal?: number
  truncated?: boolean
}
  /** tool.finished */
  'tool.finished': {
  toolCallId?: string
  name?: string
  ok?: boolean
  output?: string
  used?: number
  outputTotal?: number
  truncated?: boolean
}
  /** tool.outputDelta */
  'tool.outputDelta': {
  toolCallId?: string
  delta?: string
}
  /** tool.silentAlert */
  'tool.silentAlert': {
  toolCallId?: string
  level?: number
}
  /** round.started */
  'round.started': {
  used?: number
  limit?: number
}
  /** llm.retrying */
  'llm.retrying': {
  attempt?: number
  max?: number
  reason?: string
}
  /** usage */
  'usage': {
  prompt?: number
  completion?: number
  cached?: number
  llmCalls?: number
  dropped?: boolean
}
  /** notice */
  'notice': {
  text?: string
  level?: "info" | "warn" | "error"
}
  /** ask.permission */
  'ask.permission': PendingAsk
  /** ask.rounds */
  'ask.rounds': PendingAsk
  /** session.info */
  'session.info': {
  id?: string
  cwd?: string
  historyPath?: string | null
}
  /** session.historyLoaded */
  'session.historyLoaded': {
  count?: number
  turns?: TurnView[]
}
  /** session.contextCleared */
  'session.contextCleared': Record<string, unknown>
  /** session.compactionStarted */
  'session.compactionStarted': Record<string, unknown>
  /** session.compacted */
  'session.compacted': {
  removed?: number
  summary?: string
  kept?: TurnView[]
  turns?: TurnView[]
  totalBefore?: number
  totalAfter?: number
  seq?: number
  removedTurns?: number
  keptTurns?: number
}
  /** user.injected */
  'user.injected': {
  text?: string
}
  /** state.personaChanged */
  'state.personaChanged': {
  name?: string
}
  /** state.modeChanged */
  'state.modeChanged': {
  mode?: PermissionMode
  label?: string
}
  /** state.busyChanged */
  'state.busyChanged': {
  busy?: boolean
  queueLength?: number
}
  /** resync.required */
  'resync.required': {
  reason?: string
}
  /** round.ended */
  'round.ended': {
  used: number
  hasTools: boolean
}
}

export type HaloEventKind = keyof HaloEventPayloads
export type HaloEventPayload<K extends HaloEventKind> = HaloEventPayloads[K]

/** 错误码 → 中文说明（与 schema.errorCodes 一致）。 */
export const HaloErrorCodes = {
  'method.notFound': '方法不存在',
  'params.invalid': '参数不合法',
  'protocol.version': '协议版本不匹配',
  'state.busy': '有回合正在执行',
  'state.notConfigured': '尚未配置模型',
  'session.notFound': '会话不存在',
  'asset.notFound': '技能/人格/记忆不存在',
  'fs.notFound': '路径不存在',
  'fs.escape': '路径越界',
  'llm.error': '模型端点错误',
  'llm.timeout': '模型超时',
  'tool.failed': '工具执行失败',
  'ask.stale': '回答了一个已过期的询问',
  'config.overwriteRequired': '配置文件为旧结构或已损坏，需二次确认强制覆盖',
  'internal': '引擎内部错误',
  'notSupported': '该功能未实现',
} as const
export type HaloErrorCode = keyof typeof HaloErrorCodes

/** 全部方法名（与 schema.methods 的键集合一致）。 */
export const METHODS = [
  'system.ping',
  'system.info',
  'system.protocol',
  'system.shutdown',
  'system.diagnostics',
  'agent.prompt',
  'agent.queue',
  'agent.unqueue',
  'agent.cancel',
  'agent.answer',
  'agent.drain',
  'agent.resync',
  'session.new',
  'session.resume',
  'session.list',
  'session.rename',
  'session.delete',
  'session.clear',
  'session.compact',
  'session.setCwd',
  'state.snapshot',
  'state.setMode',
  'state.setPersona',
  'state.setMaxTurns',
  'state.setRetry',
  'settings.get',
  'settings.saveNetwork',
  'settings.saveBehavior',
  'settings.probeModels',
  'settings.mcp.get',
  'settings.mcp.save',
  'asset.skills.list',
  'asset.skills.get',
  'asset.skills.save',
  'asset.skills.delete',
  'asset.skills.run',
  'asset.themes.list',
  'asset.themes.get',
  'asset.personas.list',
  'asset.personas.get',
  'asset.personas.save',
  'asset.personas.delete',
  'asset.memory.list',
  'asset.memory.get',
  'asset.memory.save',
  'asset.memory.delete',
  'tool.list',
  'tool.undoList',
  'tool.undoRollback',
  'tool.mcpStatus',
  'fs.listDir',
  'fs.completePaths',
  'fs.validatePath',
  'update.check',
  'update.install',
  'fs.browse',
  'fs.pickFiles',
  'fs.pickSavePath',
  'fs.readImageAsBase64',
  'settings.saveProviders',
  'settings.setActive',
  'tool.result',
  'usage.query',
  'usage.summary',
  'usage.stats',
  'usage.export',
  'usage.exportFile',
  'usage.clear',
] as const

/** 全部事件 kind（与 schema.events 的键集合一致）。 */
export const EVENTS = [
  'turn.started',
  'turn.finished',
  'text.delta',
  'reasoning.delta',
  'tool.started',
  'tool.finished',
  'tool.outputDelta',
  'tool.silentAlert',
  'round.started',
  'llm.retrying',
  'usage',
  'notice',
  'ask.permission',
  'ask.rounds',
  'session.info',
  'session.historyLoaded',
  'session.contextCleared',
  'session.compactionStarted',
  'session.compacted',
  'user.injected',
  'state.personaChanged',
  'state.modeChanged',
  'state.busyChanged',
  'resync.required',
  'round.ended',
] as const

export type HaloMethod = keyof HaloMethods
export type HaloParams<M extends HaloMethod> = HaloMethods[M]['params']
export type HaloResult<M extends HaloMethod> = HaloMethods[M]['result']
